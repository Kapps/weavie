using System.Text;
using System.Text.Json;
using Weavie.Core.Editor;
using Weavie.Core.Shell;
using Weavie.Core.Workspaces;

namespace Weavie.Hosting;

/// <summary>
/// Pushes a file open to the web to reveal at a line. Shared by clickable terminal file:line links and the
/// MCP <c>openFile</c> tool; relative paths resolve against the workspace. The gate goes through the
/// session's <see cref="FileProviderService"/>, the one validated reader, so an open is confined to the
/// worktree (+ scratch) by normalized path. (The opened repo is trusted: an in-tree symlink that resolves
/// outside is still followed — confinement is by path string, not by the link target.) A relative path that
/// doesn't resolve is recovered by suffix match against the workspace index (see <see cref="OpenAsync"/>).
/// </summary>
public sealed class FileOpener {
	private readonly SessionEditorChannel _channel;
	private readonly FileProviderService _files;
	private readonly IHostBridge _bridge;
	private readonly WorkspaceFileIndex _index;

	/// <summary>Pushes files to Monaco through the session's editor <paramref name="channel"/> (so a muted session's opens are held, not posted into the foreground), reading through <paramref name="files"/> (the validated, workspace-confined reader); relative paths resolve against <paramref name="index"/>.Root, and the index backs the suffix-match recovery. A refused open toasts through <paramref name="bridge"/>.</summary>
	public FileOpener(SessionEditorChannel channel, FileProviderService files, IHostBridge bridge, WorkspaceFileIndex index) {
		ArgumentNullException.ThrowIfNull(channel);
		ArgumentNullException.ThrowIfNull(files);
		ArgumentNullException.ThrowIfNull(bridge);
		ArgumentNullException.ThrowIfNull(index);
		_channel = channel;
		_files = files;
		_bridge = bridge;
		_index = index;
	}

	/// <summary>Fire-and-forget <see cref="OpenAsync"/> — the form every reveal call site uses. A fault is logged, never swallowed.</summary>
	public void Open(string path, int line, bool preview, bool scratch) =>
		_ = OpenAsync(path, line, preview, scratch).ContinueWith(
			t => Console.Error.WriteLine($"[weavie] reveal-file failed: {t.Exception?.GetBaseException().Message}"),
			TaskContinuationOptions.OnlyOnFaulted);

	/// <summary>
	/// Pushes an <c>open-file</c> so the web opens the file (Monaco working copy, or the media pane for
	/// images/video) and reveals the 1-based line. No content rides along — the web reads disk through the fs
	/// provider. <paramref name="preview"/> opens a reusable preview tab; <paramref name="scratch"/> marks an
	/// untitled buffer shown as "Untitled-N". A relative path that doesn't resolve (a link missing its leading
	/// folders, or a bare filename) is suffix-matched against the workspace index: one hit opens it, several
	/// open Go-to-File preloaded with the reference, none toasts (as does an unresolvable rooted path).
	/// </summary>
	public async Task OpenAsync(string path, int line, bool preview, bool scratch) {
		string resolved = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(_index.Root, path));
		// Validated gate: refused for a path outside the worktree (+ scratch) or missing, so a reveal-file /
		// openFile is confined to the worktree by path (an in-tree symlink is followed — the repo is trusted).
		if (_files.CanRead(resolved)) {
			PostOpen(resolved, line, preview, scratch);
			return;
		}

		if (await TryOpenBySuffixAsync(path, line, preview, scratch).ConfigureAwait(false)) {
			return;
		}

		// A refusal toasts — the user clicked something (an omnibar row, a terminal link) and a silent drop
		// reads as the app ignoring them.
		Console.Error.WriteLine($"[weavie] reveal-file: refused or not found: {resolved}");
		_bridge.PostToWeb(ShellProtocol.BuildNotify(
			"warn", $"Couldn't open {Path.GetFileName(resolved)} — it's missing, unreadable, or outside this session's worktree."));
	}

	/// <summary>
	/// The recovery for a relative reference that didn't resolve: suffix-match it against the workspace index
	/// (off the calling thread — the walk can be slow on a big worktree). One hit re-opens through the
	/// validated gate; several push <c>focus-omnibar</c> so the user picks from Go-to-File preloaded with the
	/// reference. False (→ the caller toasts) for a rooted path, no match, or a failed walk.
	/// </summary>
	private async Task<bool> TryOpenBySuffixAsync(string path, int line, bool preview, bool scratch) {
		if (Path.IsPathRooted(path)) {
			return false;
		}

		IReadOnlyList<string> matches;
		try {
			matches = await Task.Run(() => PathSuffixMatcher.Match(_index.List(), path)).ConfigureAwait(false);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Console.Error.WriteLine($"[weavie] reveal-file: suffix match failed: {ex.Message}");
			return false;
		}

		if (matches.Count == 1) {
			// Re-enter with the matched absolute path: the validated gate still decides (rooted, so no recursion).
			await OpenAsync(matches[0], line, preview, scratch).ConfigureAwait(false);
			return true;
		}

		if (matches.Count > 1) {
			_channel.Reveal(ShellProtocol.BuildFocusOmnibar(PathSuffixMatcher.Normalize(path), line));
			return true;
		}

		return false;
	}

	private void PostOpen(string path, int line, bool preview, bool scratch) {
		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream)) {
			writer.WriteStartObject();
			writer.WriteString("type", "open-file");
			writer.WriteString("path", path);
			writer.WriteNumber("line", Math.Max(1, line));
			writer.WriteBoolean("preview", preview);
			writer.WriteBoolean("scratch", scratch);
			writer.WriteEndObject();
		}

		_channel.Reveal(Encoding.UTF8.GetString(stream.ToArray()));
	}
}
