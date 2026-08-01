using Weavie.Core.Lsp;

namespace Weavie.Core.Editor;

/// <summary>One editor-provider change: a native path and whether it was added, updated, or deleted.</summary>
/// <param name="Path">The changed file's native path.</param>
/// <param name="Kind"><c>"added"</c>, <c>"updated"</c>, or <c>"deleted"</c>.</param>
public readonly record struct FileProviderChange(string Path, string Kind);

/// <summary>Maps workspace-watcher changes onto editor file-provider changes.</summary>
public static class FileProviderChanges {
	/// <summary>Maps a watcher batch, omitting entries whose URI is invalid.</summary>
	public static FileProviderChange[] FromWatched(IReadOnlyList<WatchedFileChange> changes) {
		ArgumentNullException.ThrowIfNull(changes);
		var mapped = new List<FileProviderChange>(changes.Count);
		foreach (var change in changes) {
			if (TryToLocalPath(change.Uri, out string path)) {
				mapped.Add(new FileProviderChange(path, MapKind(change.Kind)));
			}
		}

		return [.. mapped];
	}

	private static string MapKind(FileChangeKind kind) => kind switch {
		FileChangeKind.Created => "added",
		FileChangeKind.Deleted => "deleted",
		_ => "updated",
	};

	private static bool TryToLocalPath(string uri, out string path) {
		try {
			path = new Uri(uri).LocalPath;
			return true;
		} catch (UriFormatException) {
			path = string.Empty;
			return false;
		}
	}
}
