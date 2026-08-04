using Weavie.Core.FileActivity;

namespace Weavie.Core.Lsp;

/// <summary>Projects generic workspace invalidations into language-server watched-file changes.</summary>
public static class LspFileChanges {
	/// <summary>Maps invalidations claimed by a built-in language server and omits every other file kind.</summary>
	public static WatchedFileChange[] FromInvalidations(IReadOnlyList<FileInvalidation> changes) {
		ArgumentNullException.ThrowIfNull(changes);
		return [.. changes
			.Where(change => LanguageServerCatalog.WatchedExtensions.Contains(Path.GetExtension(change.Path)))
			.Select(change => new WatchedFileChange(
				new Uri(change.Path).AbsoluteUri,
				MapKind(change.Kind)))];
	}

	private static FileChangeKind MapKind(FileInvalidationKind kind) => kind switch {
		FileInvalidationKind.Created => FileChangeKind.Created,
		FileInvalidationKind.Changed => FileChangeKind.Changed,
		FileInvalidationKind.Deleted => FileChangeKind.Deleted,
		_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown file invalidation kind."),
	};
}
