using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.TestRunning;

namespace Weavie.Core.Workspaces;

/// <summary>What <see cref="WorkspaceAutoConfig.Apply"/> wrote — the setting keys it filled (empty if none).</summary>
public sealed record AutoConfigOutcome(IReadOnlyList<string> Wrote);

/// <summary>
/// Applies a <see cref="WorkspaceDetection"/> to a workspace's settings, writing only the keys that are still
/// unset — <c>worktree.setupCommand</c> and <c>test.profile</c> — via the workspace-scoped overlay. Never
/// clobbers a value the user (or a prior run) set, and never writes an empty profile: a detection that produced
/// no rules leaves <c>test.profile</c> unset so the setup card shows and Claude fills the gap. Deterministic,
/// zero tokens. See <c>docs/concepts/workspace-autoconfig.md</c>.
/// </summary>
public sealed class WorkspaceAutoConfig {
	private readonly SettingsStore _settings;
	private readonly string _workspaceRoot;

	/// <summary>Creates an auto-config bound to <paramref name="settings"/> and <paramref name="workspaceRoot"/>.</summary>
	public WorkspaceAutoConfig(SettingsStore settings, string workspaceRoot) {
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
		_settings = settings;
		_workspaceRoot = workspaceRoot;
	}

	/// <summary>
	/// Writes the detected setup command and test profile into any still-unset workspace-scoped setting, returning
	/// which keys were written. A blank current value (unset) is filled; a set value is left untouched.
	/// </summary>
	public AutoConfigOutcome Apply(WorkspaceDetection detection) {
		ArgumentNullException.ThrowIfNull(detection);

		var wrote = new List<string>();
		if (!string.IsNullOrEmpty(detection.SetupCommand)
			&& string.IsNullOrWhiteSpace(_settings.GetString("worktree.setupCommand", _workspaceRoot))) {
			_settings.Set("worktree.setupCommand", JsonString(detection.SetupCommand), _workspaceRoot);
			wrote.Add("worktree.setupCommand");
		}

		// Only write a profile we actually derived rules for; an empty profile ([]) would falsely claim "no tests"
		// and satisfy the card. No rules → leave unset → the card offers Claude.
		if (detection.TestRules.Count > 0
			&& string.IsNullOrWhiteSpace(_settings.GetString(TestSettings.Profile, _workspaceRoot))) {
			_settings.Set(TestSettings.Profile, JsonString(TestProfile.Serialize(detection.TestRules)), _workspaceRoot);
			wrote.Add(TestSettings.Profile);
		}

		return new AutoConfigOutcome(wrote);
	}

	private static JsonElement JsonString(string value) => JsonSerializer.SerializeToElement(value);
}
