using Weavie.Core.Editor;
using Weavie.Core.Workspaces;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

/// <summary>
/// Resolves file references for client-owned navigation and host/MCP opens. Relative paths resolve against
/// the workspace, absolute ones open wherever they point. A relative path that doesn't resolve is recovered
/// by suffix match against the workspace index
/// (see <see cref="OpenAsync(string,int?,bool,bool,EditorOpenIntent)"/>). A null line means "no target": an already-open tab
/// keeps the user's scroll position instead of jumping.
/// </summary>
public sealed class FileOpener : IAsyncDisposable {
	private readonly ViewFeatureChannel _view;
	private readonly MessageFeatureChannel _notifications;
	private readonly FileProviderService _files;
	private readonly WorkspaceFileIndex _index;
	private readonly Action<string, int?, bool, bool, EditorOpenIntent> _openFile;
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
		Action<string, int?, bool, bool, EditorOpenIntent> openFile) {
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

	/// <summary>Runs <see cref="OpenAsync(string,int?,bool,bool,EditorOpenIntent,CancellationToken)"/> in this opener's owned lifetime.</summary>
	public void Open(string path, int? line, bool preview, bool scratch, EditorOpenIntent intent) =>
		_ = _background.Run(ct => OpenAsync(path, line, preview, scratch, intent, ct));

	/// <summary>
	/// Pushes an <c>open-file</c> so the web opens the file (Monaco working copy, or the media pane for
	/// images/video) and reveals the 1-based <paramref name="line"/>, or — when it is null — leaves an
	/// already-open tab at the position the user left it. No content rides along — the web reads disk through
	/// the fs provider. <paramref name="preview"/> opens a reusable preview tab; <paramref name="scratch"/> marks an
	/// untitled buffer shown as "Untitled-N". A relative path that doesn't resolve (a link missing its leading
	/// folders, or a bare filename) is suffix-matched against the workspace index: one hit opens it, several
	/// open Go-to-File preloaded with the reference, none toasts (as does an unresolvable rooted path).
	/// </summary>
	public Task OpenAsync(string path, int? line, bool preview, bool scratch, EditorOpenIntent intent) =>
		OpenAsync(path, line, preview, scratch, intent, CancellationToken.None);

	/// <summary>As <see cref="OpenAsync(string,int?,bool,bool,EditorOpenIntent)"/>, cancelled with its owning operation.</summary>
	public async Task OpenAsync(
		string path,
		int? line,
		bool preview,
		bool scratch,
		EditorOpenIntent intent,
		CancellationToken ct) {
		var result = await ResolveAsync(path, line, ct).ConfigureAwait(false);
		ct.ThrowIfCancellationRequested();
		switch (result) {
			case FileReferenceResolution.File file:
				_openFile(file.Path, file.Line, preview, scratch, intent);
				break;
			case FileReferenceResolution.Ambiguous ambiguous:
				_view.TryPublish("focusOmnibar", new { query = ambiguous.Query, line = ambiguous.Line });
				break;
			case FileReferenceResolution.Missing missing:
				_notifications.Publish("show", new { level = "warn", message = missing.Message });
				break;
			default:
				throw new InvalidOperationException("Unknown file-reference resolution.");
		}
	}

	/// <summary>Resolves a file or recovery query without opening tabs, publishing events, or taking focus.</summary>
	public async Task<FileReferenceResolution> ResolveAsync(string path, int? line, CancellationToken ct) {
		ct.ThrowIfCancellationRequested();
		string resolved = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(_index.Root, path));
		if (_files.CanRead(resolved)) {
			return new FileReferenceResolution.File(resolved, Clamp(line));
		}

		if (!Path.IsPathRooted(path)) {
			IReadOnlyList<string> matches;
			try {
				matches = await Task.Run(() => PathSuffixMatcher.Match(_index.List(), path), ct).ConfigureAwait(false);
			} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
				return new FileReferenceResolution.Missing($"Couldn't resolve {path}: {ex.Message}");
			}

			ct.ThrowIfCancellationRequested();
			if (matches.Count > 1) {
				return new FileReferenceResolution.Ambiguous(PathSuffixMatcher.Normalize(path), Clamp(line));
			}
			if (matches.Count == 1) {
				resolved = matches[0];
				if (_files.CanRead(resolved)) {
					return new FileReferenceResolution.File(resolved, Clamp(line));
				}
			}
		}

		return new FileReferenceResolution.Missing($"Couldn't open {Path.GetFileName(resolved)} — it's missing or unreadable.");
	}

	private static int? Clamp(int? line) => line is { } value ? Math.Max(1, value) : null;

	/// <inheritdoc/>
	public ValueTask DisposeAsync() => _background.DisposeAsync();
}
