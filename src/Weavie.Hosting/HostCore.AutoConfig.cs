using Weavie.Core.FileSystem;
using Weavie.Core.Workspaces;

namespace Weavie.Hosting;

// Built-in workspace auto-config: on workspace open, detect the language(s) present from the hardcoded preset
// catalog and write the unset worktree setup command + test profile — deterministic, zero model tokens. Runs
// inside the suggestion probe so the settings are already written when the card evaluates (no flash); the
// Claude-driven setup flow stays as the override / unsupported-language fallback. See
// docs/concepts/workspace-autoconfig.md.
public sealed partial class HostCore {
	private WorkspaceAutoConfig? _autoConfig;
	private readonly Lock _autoConfigGate = new();
	private string? _pendingAutoConfigToast;
	private bool _autoConfigPageReady;

	// The suggestion probe: walk + classify + write-unset, returning whether a build manifest is present (the card
	// gate). A write raises SettingChanged, which re-evaluates the card against the now-written settings.
	private bool RunAutoConfigProbe(IFileSystem fileSystem) {
		var detection = WorkspaceDetector.Detect(WorkspaceRoot, fileSystem);
		var outcome = _autoConfig!.Apply(detection);
		if (outcome.Wrote.Count > 0 && detection.ConfiguredLanguages.Count > 0) {
			QueueAutoConfigToast(AutoConfigMessage(detection.ConfiguredLanguages));
		}

		return detection.HasManifest;
	}

	// The "Configured …" toast can't be posted straight from the probe: on a fresh open the probe finishes before
	// any page has connected, and PostToWeb drops (never buffers) a push with no client. Hold it and post exactly
	// once both the write happened AND a page is connected — in whichever order those occur.
	private void QueueAutoConfigToast(string message) {
		lock (_autoConfigGate) {
			_pendingAutoConfigToast = message;
		}

		FlushAutoConfigToast();
	}

	// Called from the page's `ready` handler, once its bridge can actually receive a push.
	private void MarkAutoConfigPageReady() {
		lock (_autoConfigGate) {
			_autoConfigPageReady = true;
		}

		FlushAutoConfigToast();
	}

	private void FlushAutoConfigToast() {
		string? message;
		lock (_autoConfigGate) {
			if (!_autoConfigPageReady || _pendingAutoConfigToast is null) {
				return;
			}

			message = _pendingAutoConfigToast;
			_pendingAutoConfigToast = null;
		}

		_ui.Post(() => Notify("info", message, "workspace-autoconfig"));
	}

	private static string AutoConfigMessage(IReadOnlyList<string> languages) =>
		$"Configured this {string.Join(" + ", languages)} workspace — set up dependency install and test running. "
		+ "Re-run /mcp__weavie__setup-workspace to change it.";
}
