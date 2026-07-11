using Weavie.Core.Agents;
using Weavie.Core.Agents.Codex;
using Weavie.Core.Commands;

namespace Weavie.Hosting.Agents.Codex;

/// <summary>The session's live model / approvals / sandbox controls and slash surface, provider-neutral to the web.</summary>
public sealed partial class CodexAppServerSession : IStructuredAgentControls {
	private static readonly IReadOnlyList<AgentControlOption> ApprovalOptions = [
		new() { Id = "untrusted", Label = "Untrusted", Description = "Ask before running any command." },
		new() { Id = "on-failure", Label = "On failure", Description = "Ask only after a sandboxed command fails." },
		new() { Id = "on-request", Label = "On request", Description = "Codex asks when it wants to escalate." },
		new() { Id = "never", Label = "Never", Description = "Never ask; stay within the sandbox." },
	];

	private static readonly IReadOnlyList<AgentControlOption> SandboxOptions = [
		new() { Id = "read-only", Label = "Read only", Description = "Read files, but never write or run." },
		new() { Id = "workspace-write", Label = "Workspace write", Description = "Edit files inside the workspace." },
		new() { Id = "danger-full-access", Label = "Full access", Description = "No sandbox; full disk and network." },
	];

	private IReadOnlyList<AgentControlOption> _modelOptions = [];
	private string _catalogDefaultModel = string.Empty;
	private IReadOnlyList<CodexSkill> _skills = [];
	private string _modelOverride = string.Empty;
	private string _sandboxOverride = string.Empty;
	private string _approvalOverride = string.Empty;

	/// <inheritdoc/>
	public event Action<AgentControlState>? ControlStateChanged;

	/// <inheritdoc/>
	public AgentControlState ControlState => BuildControlState();

	/// <inheritdoc/>
	public void SetControl(string axis, string value) {
		ArgumentException.ThrowIfNullOrEmpty(axis);
		ArgumentException.ThrowIfNullOrEmpty(value);
		if (!IsValidControl(axis, value)) {
			EmitError($"'{value}' is not a valid {axis} option.");
			return;
		}

		lock (_gate) {
			switch (axis) {
				case "model": _modelOverride = value; break;
				case "approvalPolicy": _approvalOverride = value; break;
				case "sandbox": _sandboxOverride = value; break;
			}
		}

		RaiseControlState();
	}

	private string EffectiveModel() {
		lock (_gate) {
			return _modelOverride.Length > 0 ? _modelOverride : Model();
		}
	}

	private string EffectiveSandbox() {
		lock (_gate) {
			return _sandboxOverride.Length > 0 ? _sandboxOverride : Sandbox();
		}
	}

	private string EffectiveApprovalPolicy() {
		lock (_gate) {
			return _approvalOverride.Length > 0 ? _approvalOverride : ApprovalPolicy();
		}
	}

	private async Task LoadControlsAsync() {
		// Publish approvals/sandbox + the built-in slash entries up front, so the status line is present even if
		// the model/skill probes below fail (which still surface loudly through the session task runner).
		RaiseControlState();
		long modelRequest = NextRequest();
		var models = await _client.RequestAsync(modelRequest, CodexAppServerProtocol.ModelList(modelRequest, false), CancellationToken.None)
			.ConfigureAwait(false);
		if (CodexAppServerProtocol.TryReadModels(models, out var options, out string defaultModel)) {
			lock (_gate) {
				_modelOptions = options;
				_catalogDefaultModel = defaultModel;
			}
		}

		await RefreshSkillsAsync().ConfigureAwait(false);
		RaiseControlState();
	}

	private async Task RefreshSkillsAndPublishAsync() {
		await RefreshSkillsAsync().ConfigureAwait(false);
		RaiseControlState();
	}

	private async Task RefreshSkillsAsync() {
		long skillsRequest = NextRequest();
		var skills = await _client.RequestAsync(skillsRequest, CodexAppServerProtocol.SkillsList(skillsRequest, _context.Workspace), CancellationToken.None)
			.ConfigureAwait(false);
		if (CodexAppServerProtocol.TryReadSkills(skills, out var parsed)) {
			lock (_gate) {
				_skills = parsed;
			}
		}
	}

	private AgentControlState BuildControlState() {
		string model, sandbox, approval, catalogDefault;
		IReadOnlyList<AgentControlOption> modelOptions;
		IReadOnlyList<CodexSkill> skills;
		lock (_gate) {
			model = _modelOverride.Length > 0 ? _modelOverride : Model();
			sandbox = _sandboxOverride.Length > 0 ? _sandboxOverride : Sandbox();
			approval = _approvalOverride.Length > 0 ? _approvalOverride : ApprovalPolicy();
			modelOptions = _modelOptions;
			catalogDefault = _catalogDefaultModel;
			skills = _skills;
		}

		List<AgentControlAxis> axes = [
			Axis("model", "Model", model.Length > 0 ? model : catalogDefault, modelOptions),
			Axis("approvalPolicy", "Approvals", approval, ApprovalOptions),
			Axis("sandbox", "Sandbox", sandbox, SandboxOptions),
		];

		List<AgentSlashEntry> slash = [
			Builtin("model", "Switch the model for this session", CoreCommands.SelectModel),
			Builtin("approvals", "Change when Codex asks for approval", CoreCommands.SelectApprovalPolicy),
			Builtin("sandbox", "Change what Codex is allowed to touch", CoreCommands.SelectSandbox),
			.. skills.Select(SkillEntry),
		];

		return new AgentControlState { Axes = axes, Slash = slash };
	}

	/// <summary>Resolves staged skill names to the session's known skills, dropping any that are no longer available.</summary>
	private IReadOnlyList<CodexSkill> ResolveSkills(IReadOnlyList<string> names) {
		if (names.Count == 0) {
			return [];
		}

		lock (_gate) {
			return [.. names.Select(name => _skills.FirstOrDefault(skill => skill.Name == name)).OfType<CodexSkill>()];
		}
	}

	private bool IsValidControl(string axis, string value) {
		switch (axis) {
			case "model":
				lock (_gate) {
					return _modelOptions.Any(option => option.Id == value);
				}
			case "approvalPolicy":
				return ApprovalOptions.Any(option => option.Id == value);
			case "sandbox":
				return SandboxOptions.Any(option => option.Id == value);
			default:
				return false;
		}
	}

	private void RaiseControlState() => ControlStateChanged?.Invoke(BuildControlState());

	private static AgentControlAxis Axis(string id, string label, string value, IReadOnlyList<AgentControlOption> options) {
		var current = options.FirstOrDefault(option => option.Id == value);
		return new AgentControlAxis {
			Id = id,
			Label = label,
			Value = value,
			ValueLabel = current?.Label ?? (value.Length > 0 ? value : "default"),
			Options = options,
		};
	}

	private static AgentSlashEntry Builtin(string name, string description, string commandId) =>
		new() { Id = $"builtin:{name}", Name = name, Description = description, CommandId = commandId };

	private static AgentSlashEntry SkillEntry(CodexSkill skill) =>
		new() { Id = $"skill:{skill.Name}", Name = skill.Name, Description = skill.Description, SkillName = skill.Name };
}
