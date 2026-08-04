using Weavie.Core.FileActivity;

namespace Weavie.Core.Editor;

/// <summary>One editor-provider change: a native path and whether it was added, updated, or deleted.</summary>
/// <param name="Path">The changed file's native path.</param>
/// <param name="Kind"><c>"added"</c>, <c>"updated"</c>, or <c>"deleted"</c>.</param>
public readonly record struct FileProviderChange(string Path, string Kind);

/// <summary>Maps workspace-watcher changes onto editor file-provider changes.</summary>
public static class FileProviderChanges {
	/// <summary>Maps a native-path invalidation batch.</summary>
	public static FileProviderChange[] FromInvalidations(IReadOnlyList<FileInvalidation> changes) {
		ArgumentNullException.ThrowIfNull(changes);
		return [.. changes.Select(change => new FileProviderChange(change.Path, MapKind(change.Kind)))];
	}

	private static string MapKind(FileInvalidationKind kind) => kind switch {
		FileInvalidationKind.Created => "added",
		FileInvalidationKind.Deleted => "deleted",
		_ => "updated",
	};
}
