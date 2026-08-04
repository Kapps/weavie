using Weavie.Core.FileSystem;

namespace Weavie.Core.FileActivity;

/// <summary>A completed or observed file activity admitted by one session.</summary>
/// <param name="Sequence">The activity's monotonic sequence within its owning session.</param>
public abstract record FileActivityFact(long Sequence);

/// <summary>A host-backed editor buffer finished saving.</summary>
/// <param name="Sequence">The activity's monotonic sequence within its owning session.</param>
/// <param name="Path">The saved file's normalized absolute path.</param>
/// <param name="Revision">The post-save file metadata.</param>
public sealed record BufferSaved(long Sequence, string Path, FileStat Revision)
	: FileActivityFact(Sequence);

/// <summary>A file's completed state is known to have changed.</summary>
/// <param name="Sequence">The activity's monotonic sequence within its owning session.</param>
/// <param name="Path">The changed file's normalized absolute path.</param>
/// <param name="Revision">The observed file metadata.</param>
public sealed record FileChanged(long Sequence, string Path, FileStat Revision)
	: FileActivityFact(Sequence);

/// <summary>A file's completed state is known to have been deleted.</summary>
/// <param name="Sequence">The activity's monotonic sequence within its owning session.</param>
/// <param name="Path">The deleted file's normalized absolute path.</param>
public sealed record FileDeleted(long Sequence, string Path) : FileActivityFact(Sequence);

/// <summary>A debounced watcher batch invalidated filesystem-backed consumers.</summary>
/// <param name="Sequence">The activity's monotonic sequence within its owning session.</param>
/// <param name="Changes">The normalized paths and invalidation kinds in this batch.</param>
public sealed record FilesInvalidated(long Sequence, IReadOnlyList<FileInvalidation> Changes)
	: FileActivityFact(Sequence);

/// <summary>The watcher-observed filesystem transition for one path.</summary>
public enum FileInvalidationKind {
	/// <summary>The path appeared.</summary>
	Created = 1,

	/// <summary>The path's contents or metadata changed.</summary>
	Changed = 2,

	/// <summary>The path disappeared.</summary>
	Deleted = 3,
}

/// <summary>One native-path invalidation observed by the session's workspace watcher.</summary>
/// <param name="Path">The normalized absolute path.</param>
/// <param name="Kind">The observed filesystem transition.</param>
public readonly record struct FileInvalidation(string Path, FileInvalidationKind Kind);

/// <summary>Identifies admitted activity and completes after its consumers settle.</summary>
/// <param name="Sequence">The admitted activity's session sequence.</param>
/// <param name="Settled">Completion of every snapshotted consumer or its failure handler.</param>
public sealed record FileActivityTicket(long Sequence, Task Settled);

/// <summary>A file-activity consumer failure presented to its required failure handler.</summary>
/// <param name="Consumer">The registered consumer name.</param>
/// <param name="Fact">The fact the consumer failed to process.</param>
/// <param name="Error">The consumer exception.</param>
public sealed record FileActivityFailure(string Consumer, FileActivityFact Fact, Exception Error);
