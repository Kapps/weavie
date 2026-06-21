namespace Weavie.Core.FileSystem;

/// <summary>
/// Shared persistence helpers for the JSON-backed config stores (sessions, layout, theme overrides, recents,
/// worktree registry, …). They all follow the same recovery contract: a malformed file is copied aside to
/// <c>&lt;file&gt;.bad</c> and the store resets rather than throwing on startup.
/// </summary>
public static class JsonStoreFile {
	/// <summary>
	/// Best-effort copy of a malformed <paramref name="path"/> to <c>&lt;path&gt;.bad</c> before it is reset.
	/// A failure to back up is logged (with the store's <paramref name="tag"/>) but never thrown — losing the
	/// corrupt copy must not block recovery.
	/// </summary>
	public static void BackupBad(IFileSystem fileSystem, string path, string text, string tag, Action<string>? log) {
		try {
			fileSystem.WriteAllText(path + ".bad", text);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			log?.Invoke($"[{tag}] could not back up malformed file: {ex.Message}");
		}
	}
}
