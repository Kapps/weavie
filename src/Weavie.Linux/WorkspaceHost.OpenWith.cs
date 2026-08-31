using Weavie.Core;
using Weavie.Core.Git;
using Weavie.Hosting.Desktop;
using Weavie.Linux.Native;

namespace Weavie.Linux;

// Paths the OS handed this launch, and the handovers a second launch makes. Linux shows one workspace per
// process, so a path belonging to a different workspace is declined and the caller boots it in its own window.
internal sealed partial class WorkspaceHost {
	private InstanceServer? _instanceServer;
	private IReadOnlyList<string> _launchPaths = [];

	/// <summary>The paths this launch was asked to open, resolved before the window exists.</summary>
	internal void SetLaunchPaths(IReadOnlyList<string> paths) {
		ArgumentNullException.ThrowIfNull(paths);
		_launchPaths = paths;
	}

	// The workspace a launch path implies, or null to keep the ordinary reopen-last behavior.
	private string? LaunchWorkspace() =>
		_launchPaths.Count == 0 ? null : ResolveTarget(_launchPaths[0]).Root;

	private void StartInstanceServer() {
		_instanceServer = new InstanceServer(WeaviePaths.Root, HandleHandoff, Log);
		_instanceServer.Start();
	}

	// Runs off the UI thread: only decides, and marshals the open onto the main loop.
	private HandoffReply HandleHandoff(IReadOnlyList<string> paths) {
		if (paths.Count == 0) {
			return new HandoffReply(true, string.Empty);
		}

		var target = ResolveTarget(paths[0]);
		if (_core is null || !string.Equals(target.Root, _core.WorkspaceRoot, StringComparison.Ordinal)) {
			return new HandoffReply(false, target.Root);
		}

		foreach (string path in paths) {
			var open = ResolveTarget(path);
			if (open.File is { } file) {
				GtkMain.Invoke(() => _core?.RequestOpenPath(file, 1));
			}
		}

		GtkMain.Invoke(() => ActivateWindow(null));
		return new HandoffReply(true, string.Empty);
	}

	private OpenTarget ResolveTarget(string path) =>
		OpenTargetResolver.Resolve(path, _core is null ? [] : [_core.WorkspaceRoot], GitToplevel(path));

	private static string? GitToplevel(string path) {
		string? directory = Directory.Exists(path) ? path : Path.GetDirectoryName(Path.GetFullPath(path));
		return directory is null
			? null
			: new GitService().FindToplevelAsync(directory).GetAwaiter().GetResult();
	}

	private void OpenLaunchPaths() {
		foreach (string path in _launchPaths) {
			if (ResolveTarget(path).File is { } file) {
				_core?.RequestOpenPath(file, 1);
			}
		}
	}
}
