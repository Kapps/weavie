namespace Weavie.Core.Lsp;

/// <summary>An LSP <c>FileChangeType</c>: a file was created, changed, or deleted.</summary>
public enum FileChangeKind {
	/// <summary>The file was created (LSP <c>FileChangeType.Created</c> = 1).</summary>
	Created = 1,

	/// <summary>The file's contents changed (LSP <c>FileChangeType.Changed</c> = 2).</summary>
	Changed = 2,

	/// <summary>The file was deleted (LSP <c>FileChangeType.Deleted</c> = 3).</summary>
	Deleted = 3,
}

/// <summary>One watched-file change prepared for LSP delivery.</summary>
/// <param name="Uri">The changed file's <c>file://</c> URI.</param>
/// <param name="Kind">The kind of change.</param>
public readonly record struct WatchedFileChange(string Uri, FileChangeKind Kind);
