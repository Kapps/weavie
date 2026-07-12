using Weavie.Core.Agents;
using Weavie.Core.Agents.Codex;
using Weavie.Core.Commands;

namespace Weavie.Hosting.Agents.Codex;

/// <summary>The session's live model / effort / speed / approvals / sandbox controls and slash surface, provider-neutral to the web.</summary>
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

	// The off end of the Fast toggle: id "standard" clears any tier on Codex; it's always a valid service-tier choice.
	private static readonly AgentControlOption StandardTier =
		new() { Id = "standard", Label = "Standard", Description = "Normal speed and usage." };

	private static readonly CodexModelEntry NoModel = new() {
		Model = new AgentControlOption { Id = string.Empty, Label = string.Empty },
		IsDefault = false,
		DefaultEffort = string.Empty,
		Efforts = [],
		DefaultServiceTier = string.Empty,
		ServiceTiers = [],
	};

	private IReadOnlyList<CodexModelEntry> _catalog = [];
	private IReadOnlyList<CodexSkill> _skills = [];
	private string _modelOverride = string.Empty;
	private string _effortOverride = string.Empty;
	private string _serviceTierOverride = string.Empty;
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
				case "model": _modelOverride = value; DropInvalidDerivedOverrides(); break;
				case "effort": _effortOverride = value; break;
				case "serviceTier": _serviceTierOverride = value; break;
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

	private string EffectiveEffort() {
		lock (_gate) {
			return EffortOverrideOrSetting(CurrentModelEntryLocked());
		}
	}

	private string EffectiveServiceTier() {
		lock (_gate) {
			return ServiceTierOverrideOrSetting(CurrentModelEntryLocked());
		}
	}

	private string EffectiveSandbox() {
		lock (_gate) {
			return SandboxLocked();
		}
	}

	private string EffectiveApprovalPolicy() {
		lock (_gate) {
			return ApprovalPolicyLocked();
		}
	}

	private async Task LoadControlsAsync() {
		// Publish approvals/sandbox + the built-in slash entries up front, so the status line is present even if
		// the model/skill probes below fail (which still surface loudly through the session task runner).
		RaiseControlState();
		long modelRequest = NextRequest();
		var models = await _client.RequestAsync(modelRequest, CodexAppServerProtocol.ModelList(modelRequest, false), CancellationToken.None)
			.ConfigureAwait(false);
		if (CodexModelCatalog.TryReadModelCatalog(models, out var catalog)) {
			lock (_gate) {
				_catalog = catalog;
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
		lock (_gate) {
			string modelValue = CurrentModelIdLocked();
			var entry = CurrentModelEntryLocked();
			IReadOnlyList<AgentControlOption> modelOptions = [.. _catalog.Select(model => model.Model)];
			var tierOptions = ServiceTierOptions(entry);

			List<AgentControlAxis> axes = [
				Axis("model", "Model", modelValue, modelOptions),
			];
			// Effort and Speed are per-model: shown only when the current model actually offers a choice there.
			if (entry.Efforts.Count > 1) {
				axes.Add(Axis("effort", "Effort", EffortDisplay(entry), entry.Efforts));
			}

			if (tierOptions.Count > 1) {
				axes.Add(Axis("serviceTier", "Speed", ServiceTierDisplay(entry), tierOptions) with { Toggle = tierOptions.Count == 2 });
			}

			axes.Add(Axis("approvalPolicy", "Approvals", ApprovalPolicyLocked(), ApprovalOptions));
			axes.Add(Axis("sandbox", "Sandbox", SandboxLocked(), SandboxOptions));

			List<AgentSlashEntry> slash = [
				Builtin("model", "Switch the model for this session", CoreCommands.SelectModel),
			];
			if (entry.Efforts.Count > 1) {
				slash.Add(Builtin("effort", "Change how hard Codex reasons", CoreCommands.SelectEffort));
			}

			if (tierOptions.Count > 1) {
				slash.Add(Builtin("fast", "Toggle Fast Mode (faster responses)", CoreCommands.ToggleFastMode));
			}

			slash.AddRange([
				Builtin("approvals", "Change when Codex asks for approval", CoreCommands.SelectApprovalPolicy),
				Builtin("sandbox", "Change what Codex is allowed to touch", CoreCommands.SelectSandbox),
				.. _skills.Select(SkillEntry),
			]);

			return new AgentControlState { Axes = axes, Slash = slash };
		}
	}

	// The service-tier axis offers Standard plus whatever non-standard tiers the model exposes.
	private static IReadOnlyList<AgentControlOption> ServiceTierOptions(CodexModelEntry entry) =>
		entry.ServiceTiers.Count == 0 ? [] : [StandardTier, .. entry.ServiceTiers];

	// The current model id: override else setting else catalog default (empty before the catalog loads and nothing is set).
	private string CurrentModelIdLocked() {
		string id = _modelOverride.Length > 0 ? _modelOverride : Model();
		return id.Length > 0 ? id : _catalog.FirstOrDefault(model => model.IsDefault)?.Model.Id ?? string.Empty;
	}

	// The current model's catalog entry, or NoModel when the id isn't in the catalog (unknown model, or pre-load).
	private CodexModelEntry CurrentModelEntryLocked() =>
		_catalog.FirstOrDefault(model => model.Model.Id == CurrentModelIdLocked()) ?? NoModel;

	// The effort to send: the override (always model-valid) else the setting, scoped to the model. A setting this
	// model supports is used; one another model supports is a per-model gap, dropped to empty (wire omits → model
	// default) so a hidden axis can't strand it; one no model supports (unset or a typo) passes through — empty
	// omits, a typo reaches Codex and surfaces loudly rather than being silently swallowed.
	private string EffortOverrideOrSetting(CodexModelEntry entry) =>
		_effortOverride.Length > 0 ? _effortOverride : ScopeSettingToModel(Effort(), entry.Efforts, model => model.Efforts);

	private string ServiceTierOverrideOrSetting(CodexModelEntry entry) =>
		_serviceTierOverride.Length > 0 ? _serviceTierOverride : ScopeSettingToModel(ServiceTier(), entry.ServiceTiers, model => model.ServiceTiers);

	private string ScopeSettingToModel(string setting, IReadOnlyList<AgentControlOption> here, Func<CodexModelEntry, IReadOnlyList<AgentControlOption>> of) {
		if (here.Any(option => option.Id == setting)) {
			return setting;
		}

		return _catalog.Any(model => of(model).Any(option => option.Id == setting)) ? string.Empty : setting;
	}

	private string EffortDisplay(CodexModelEntry entry) {
		string effort = EffortOverrideOrSetting(entry);
		return effort.Length > 0 ? effort : entry.DefaultEffort;
	}

	private string ServiceTierDisplay(CodexModelEntry entry) {
		string tier = ServiceTierOverrideOrSetting(entry);
		return tier.Length > 0 ? tier : StandardTier.Id;
	}

	private string SandboxLocked() => _sandboxOverride.Length > 0 ? _sandboxOverride : Sandbox();

	private string ApprovalPolicyLocked() => _approvalOverride.Length > 0 ? _approvalOverride : ApprovalPolicy();

	// After a model change, replace any effort/tier override the new model doesn't support with an explicit value
	// that clears the stale one on Codex's side: the effort resets to the new model's default, the tier to Standard
	// (a JSON null). Both are sent on the next turn/start, so an unsupported value can never linger on the thread.
	private void DropInvalidDerivedOverrides() {
		var entry = CurrentModelEntryLocked();
		if (_effortOverride.Length > 0 && entry.Efforts.All(option => option.Id != _effortOverride)) {
			_effortOverride = entry.DefaultEffort;
		}

		if (_serviceTierOverride.Length > 0
			&& _serviceTierOverride != StandardTier.Id
			&& entry.ServiceTiers.All(option => option.Id != _serviceTierOverride)) {
			_serviceTierOverride = StandardTier.Id;
		}
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
					return _catalog.Any(model => model.Model.Id == value);
				}
			case "effort":
				lock (_gate) {
					return CurrentModelEntryLocked().Efforts.Any(option => option.Id == value);
				}
			case "serviceTier":
				lock (_gate) {
					return ServiceTierOptions(CurrentModelEntryLocked()).Any(option => option.Id == value);
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
