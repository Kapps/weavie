using System.Text.Json;

namespace Weavie.Core.FileSystem;

/// <summary>
/// Shared persistence helpers for the JSON-backed config stores (sessions, layout, theme overrides, recents,
/// worktree registry, …). They all follow the same recovery contract: a malformed file is copied aside to
/// <c>&lt;file&gt;.bad</c> and the store resets rather than throwing on startup.
/// </summary>
public static class JsonStoreFile {
	/// <summary>Loads one JSON store with the shared missing, unreadable, and malformed-file recovery policy.</summary>
	public static T Load<T>(
		IFileSystem fileSystem,
		string path,
		Func<string, T> deserialize,
		Func<T> empty,
		Action<string>? log) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrEmpty(path);
		ArgumentNullException.ThrowIfNull(deserialize);
		ArgumentNullException.ThrowIfNull(empty);
		string tag = Path.GetFileNameWithoutExtension(path);
		if (!fileSystem.FileExists(path)) {
			return empty();
		}

		string text;
		try {
			text = fileSystem.ReadAllText(path);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			log?.Invoke($"[{tag}] could not read {path}: {ex.Message}; starting empty");
			return empty();
		}

		try {
			return deserialize(text);
		} catch (JsonException ex) {
			log?.Invoke($"[{tag}] {path} is malformed ({ex.Message}); backing up to {Path.GetFileName(path)}.bad and resetting");
			BackupBad(fileSystem, path, text, log);
			return empty();
		}
	}

	/// <summary>Atomically writes one JSON store, reporting persistence failures through its diagnostic log.</summary>
	public static void Persist(
		IFileSystem fileSystem,
		string path,
		string text,
		Action<string>? log) => Persist(fileSystem, path, text, static () => { }, log);

	/// <summary>Atomically writes one JSON store and runs <paramref name="written"/> inside the failure envelope.</summary>
	public static void Persist(
		IFileSystem fileSystem,
		string path,
		string text,
		Action written,
		Action<string>? log) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrEmpty(path);
		ArgumentNullException.ThrowIfNull(text);
		ArgumentNullException.ThrowIfNull(written);
		try {
			fileSystem.WriteAllTextAtomic(path, text);
			written();
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			log?.Invoke($"[{Path.GetFileNameWithoutExtension(path)}] could not persist: {ex.Message}");
		}
	}

	/// <summary>
	/// Best-effort copy of a malformed <paramref name="path"/> to <c>&lt;path&gt;.bad</c> before it is reset.
	/// A failure to back up is logged under the store's file name but never thrown — losing the corrupt copy
	/// must not block recovery.
	/// </summary>
	public static void BackupBad(IFileSystem fileSystem, string path, string text, Action<string>? log) {
		try {
			fileSystem.WriteAllText(path + ".bad", text);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			log?.Invoke($"[{Path.GetFileNameWithoutExtension(path)}] could not back up malformed file: {ex.Message}");
		}
	}
}
