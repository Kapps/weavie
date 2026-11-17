using Weavie.Core.FileSystem;
using Weavie.Core.Workspaces;

namespace Weavie.Hosting;

// Built-in auto-config: when a repo joins the workspace, detect the language(s) present from the hardcoded preset
// catalog and write its unset worktree setup command + test profile — deterministic, zero model tokens. Runs
// inside the repo's suggestion probe so the settings are already written when the card evaluates (no flash); the
// Claude-driven setup flow stays as the override / unsupported-language fallback. See
// docs/concepts/workspace-autoconfig.md.
public sealed partial class HostCore {
	private readonly Lock _autoConfigGate = new();
	private readonly List<(string Message, string Key)> _pendingAutoConfigToasts = [];
	private bool _autoConfigPageReady;

	// The suggestion probe: walk + classify + write-unset, returning whether a build manifest is present (the card
	// gate). A write raises SettingChanged, which re-evaluates the card against the now-written settings.
	private bool RunAutoConfigProbe(string root, WorkspaceAutoConfig autoConfig, IFileSystem fileSystem) {
		var detection = WorkspaceDetector.Detect(root, fileSystem);
		var outcome = autoConfig.Apply(detection);
		if (outcome.Wrote.Count > 0 && detection.ConfiguredLanguages.Count > 0) {
			QueueAutoConfigToast(
				$"Configured {string.Join(" + ", detection.ConfiguredLanguages)} dependency install and test running "
				+ $"for '{WorkspaceNaming.Label(root)}'. Re-run /mcp__weavie__setup-workspace to change it.",
				$"workspace-autoconfig:{WorkspaceId.ForPath(root).Value}");
		}

		return detection.HasManifest;
	}

	// The "Configured …" toast can't be posted straight from the probe: on a fresh open the probe finishes before
	// any page has connected, and Broadcast drops (never buffers) a push with no client. Hold it and post exactly
	// once both the write happened AND a page is connected — in whichever order those occur.
	private void QueueAutoConfigToast(string message, string key) {
		lock (_autoConfigGate) {
			_pendingAutoConfigToasts.Add((message, key));
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
		(string Message, string Key)[] toasts;
		lock (_autoConfigGate) {
			if (!_autoConfigPageReady) {
				return;
			}

			toasts = [.. _pendingAutoConfigToasts];
			_pendingAutoConfigToasts.Clear();
		}

		foreach (var (message, key) in toasts) {
			_ui.Post(() => Notify("info", message, key));
		}
	}
}
