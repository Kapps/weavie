using Weavie.Core.Editor;
using Weavie.Core.Workspaces;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

/// <summary>
/// Pushes a file open to the web to reveal at a line. Shared by clickable terminal file:line links and the
/// MCP <c>openFile</c> tool; relative paths resolve against the workspace. The gate goes through the
/// session's <see cref="FileProviderService"/>, the one validated reader, so an open is confined to the
/// worktree (+ scratch) by normalized path. (The opened repo is trusted: an in-tree symlink that resolves
/// outside is still followed — confinement is by path string, not by the link target.) A relative path that
/// doesn't resolve is recovered by suffix match against the workspace index
/// (see <see cref="OpenAsync(string,int,bool,bool)"/>).
/// </summary>
public sealed class FileOpener : IAsyncDisposable {
	private readonly ViewFeatureChannel _view;
	private readonly MessageFeatureChannel _notifications;
	private readonly FileProviderService _files;
	private readonly WorkspaceFileIndex _index;
	private readonly Action<string, int, bool, bool> _openFile;
	private readonly SessionTaskScope _background =
		new(message => Console.Error.WriteLine($"[weavie] file opener: {message}"));

	/// <summary>Resolves durable editor opens through <paramref name="openFile"/>, presentation-only prompts
	/// through <paramref name="view"/>, reads through <paramref name="files"/>, and resolves relative paths
	/// against <paramref name="index"/>.</summary>
	public FileOpener(
		ViewFeatureChannel view,
		MessageFeatureChannel notifications,
		FileProviderService files,
		WorkspaceFileIndex index,
		Action<string, int, bool, bool> openFile) {
		ArgumentNullException.ThrowIfNull(view);
		ArgumentNullException.ThrowIfNull(notifications);
		ArgumentNullException.ThrowIfNull(files);
		ArgumentNullException.ThrowIfNull(index);
		ArgumentNullException.ThrowIfNull(openFile);
		_view = view;
		_notifications = notifications;
		_files = files;
		_index = index;
		_openFile = openFile;
	}

	/// <summary>Runs <see cref="OpenAsync(string,int,bool,bool,CancellationToken)"/> in this opener's owned lifetime.</summary>
	public void Open(string path, int line, bool preview, bool scratch) =>
		_ = _background.Run(ct => OpenAsync(path, line, preview, scratch, ct));

	/// <summary>
	/// Pushes an <c>open-file</c> so the web opens the file (Monaco working copy, or the media pane for
	/// images/video) and reveals the 1-based line. No content rides along — the web reads disk through the fs
	/// provider. <paramref name="preview"/> opens a reusable preview tab; <paramref name="scratch"/> marks an
	/// untitled buffer shown as "Untitled-N". A relative path that doesn't resolve (a link missing its leading
	/// folders, or a bare filename) is suffix-matched against the workspace index: one hit opens it, several
	/// open Go-to-File preloaded with the reference, none toasts (as does an unresolvable rooted path).
	/// </summary>
	public Task OpenAsync(string path, int line, bool preview, bool scratch) =>
		OpenAsync(path, line, preview, scratch, CancellationToken.None);

	/// <summary>As <see cref="OpenAsync(string,int,bool,bool)"/>, cancelled with its owning operation.</summary>
	public async Task OpenAsync(
		string path,
		int line,
		bool preview,
		bool scratch,
		CancellationToken ct) {
		ct.ThrowIfCancellationRequested();
		string resolved = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(_index.Root, path));
		// Validated gate: refused for a path outside the worktree (+ scratch) or missing, so a reveal-file /
		// openFile is confined to the worktree by path (an in-tree symlink is followed — the repo is trusted).
		if (_files.CanRead(resolved)) {
			PostOpen(resolved, line, preview, scratch);
			return;
		}

		if (await TryOpenBySuffixAsync(path, line, preview, scratch, ct).ConfigureAwait(false)) {
			return;
		}

		// A refusal toasts — the user clicked something (an omnibar row, a terminal link) and a silent drop
		// reads as the app ignoring them.
		Console.Error.WriteLine($"[weavie] reveal-file: refused or not found: {resolved}");
		_notifications.Publish("show", new {
			level = "warn",
			message = $"Couldn't open {Path.GetFileName(resolved)} — it's missing, unreadable, or outside this session's worktree.",
		});
	}

	/// <summary>
	/// The recovery for a relative reference that didn't resolve: suffix-match it against the workspace index
	/// (off the calling thread — the walk can be slow on a big worktree). One hit re-opens through the
	/// validated gate; several push <c>focus-omnibar</c> so the user picks from Go-to-File preloaded with the
	/// reference. False (→ the caller toasts) for a rooted path, no match, or a failed walk.
	/// </summary>
	private async Task<bool> TryOpenBySuffixAsync(
		string path,
		int line,
		bool preview,
		bool scratch,
		CancellationToken ct) {
		if (Path.IsPathRooted(path)) {
			return false;
		}

		IReadOnlyList<string> matches;
		try {
			matches = await Task
				.Run(() => PathSuffixMatcher.Match(_index.List(), path), ct)
				.ConfigureAwait(false);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Console.Error.WriteLine($"[weavie] reveal-file: suffix match failed: {ex.Message}");
			return false;
		}

		if (matches.Count == 1) {
			// Re-enter with the matched absolute path: the validated gate still decides (rooted, so no recursion).
			await OpenAsync(matches[0], line, preview, scratch, ct).ConfigureAwait(false);
			return true;
		}

		if (matches.Count > 1) {
			_view.TryPublish("focusOmnibar", new {
				query = PathSuffixMatcher.Normalize(path),
				line = Math.Max(1, line),
			});
			return true;
		}

		return false;
	}

	private void PostOpen(string path, int line, bool preview, bool scratch) =>
		_openFile(path, Math.Max(1, line), preview, scratch);

	/// <inheritdoc/>
	public ValueTask DisposeAsync() => _background.DisposeAsync();
}
