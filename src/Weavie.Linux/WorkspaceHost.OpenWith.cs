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
	private string? LaunchWorkspace() => _launchPaths.Count == 0
		? null
		: OpenTargetResolver.Resolve(_launchPaths[0], [], new ToplevelCache().For(_launchPaths[0])).Root;

	private void StartInstanceServer() {
		var server = new InstanceServer(WeaviePaths.Root, HandleHandoff, Log);
		// Another window already serves this root; ours simply doesn't, rather than taking the endpoint from it.
		if (server.TryStart()) {
			_instanceServer = server;
		}
	}

	private void StopInstanceServer() {
		_instanceServer?.DisposeAsync().AsTask().GetAwaiter().GetResult();
		_instanceServer = null;
	}

	// Runs off the pipe thread: decides with the shared policy, and marshals each open onto the main loop.
	private HandoffReply HandleHandoff(HandoffRequest request) {
		var toplevel = new ToplevelCache();
		var reply = DesktopHandoff.Offer(
			request.Paths,
			Volatile.Read(ref _core)?.WorkspaceRoot,
			toplevel.For,
			file => GtkMain.Invoke(() => _core?.RequestOpenPath(file)));
		if (reply.Accepted) {
			// The token belongs to the launch that received the click; without it the compositor refuses the raise.
			string token = request.ActivationToken;
			GtkMain.Invoke(() => ActivateWindow(token.Length == 0 ? null : token));
		}

		return reply;
	}

	private void OpenLaunchPaths() {
		var toplevel = new ToplevelCache();
		foreach (string path in _launchPaths) {
			var target = OpenTargetResolver.Resolve(
				path,
				_core is null ? [] : [_core.WorkspaceRoot],
				toplevel.For(path));
			if (target.File is { } file) {
				_core?.RequestOpenPath(file);
			}
		}
	}

	// One `git rev-parse` per directory, not per path: a multi-file Open With is one selection, usually one
	// folder. A missing directory or an unavailable git means no repository, never a dead launch.
	private sealed class ToplevelCache {
		private readonly Dictionary<string, string?> _byDirectory = new(StringComparer.Ordinal);

		public string? For(string path) {
			string? directory = Directory.Exists(path) ? path : Path.GetDirectoryName(Path.GetFullPath(path));
			if (directory is null || !Directory.Exists(directory)) {
				return null;
			}

			if (_byDirectory.TryGetValue(directory, out string? cached)) {
				return cached;
			}

			string? toplevel;
			try {
				toplevel = new GitService().FindToplevelAsync(directory).GetAwaiter().GetResult();
			} catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException) {
				toplevel = null;
			}

			_byDirectory[directory] = toplevel;
			return toplevel;
		}
	}
}
