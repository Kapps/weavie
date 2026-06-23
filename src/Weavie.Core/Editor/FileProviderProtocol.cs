using System.Text;
using System.Text.Json;
using Weavie.Core.FileSystem;
using Weavie.Core.Lsp;

namespace Weavie.Core.Editor;

/// <summary>One entry in an <c>fs-change</c> batch: a file's native path and what happened to it.</summary>
/// <param name="Path">The changed file's native (OS) path; the web resolves it with <c>monaco.Uri.file</c>.</param>
/// <param name="Kind"><c>"added"</c>, <c>"updated"</c>, or <c>"deleted"</c>.</param>
public readonly record struct FileProviderChange(string Path, string Kind);

/// <summary>
/// Builds the host→web JSON for the editor's host-backed <c>file://</c> provider. Uses <see cref="Utf8JsonWriter"/>
/// rather than <c>JsonSerializer</c> to stay trim-safe on macOS (reflection serializer is IL2026-unsafe there).
/// Shapes mirror the web's <c>WebBoundMessage</c> union in <c>src/web/src/bridge.ts</c>.
/// </summary>
public static class FileProviderProtocol {
	/// <summary>
	/// Reply to <c>fs-stat</c>: existence + kind + mtime/ctime (ms since epoch) + size. A missing path is a normal
	/// <c>exists:false</c> answer, not an error — the provider turns that into a FileNotFound throw.
	/// </summary>
	public static string StatResult(string id, FileStat stat) {
		ArgumentException.ThrowIfNullOrEmpty(id);
		return Build(writer => {
			writer.WriteString("type", "fs-stat-result");
			writer.WriteString("id", id);
			writer.WriteBoolean("ok", true);
			writer.WriteBoolean("exists", stat.Exists);
			writer.WriteBoolean("isDir", stat.IsDirectory);
			writer.WriteNumber("mtimeMs", stat.MtimeMs);
			writer.WriteNumber("ctimeMs", stat.CtimeMs);
			writer.WriteNumber("size", stat.Size);
		});
	}

	/// <summary>Reply to <c>fs-read</c> with the file's content and post-read stat (so the provider sets its etag).</summary>
	public static string ReadResult(string id, string content, FileStat stat) {
		ArgumentException.ThrowIfNullOrEmpty(id);
		ArgumentNullException.ThrowIfNull(content);
		return Build(writer => {
			writer.WriteString("type", "fs-read-result");
			writer.WriteString("id", id);
			writer.WriteBoolean("ok", true);
			writer.WriteString("content", content);
			writer.WriteNumber("mtimeMs", stat.MtimeMs);
			writer.WriteNumber("size", stat.Size);
		});
	}

	/// <summary>
	/// Reply to <c>fs-read</c> for a missing (or out-of-workspace) path: <c>code:"FileNotFound"</c>, which the
	/// provider raises so the overlay falls through to its empty in-memory layer.
	/// </summary>
	public static string ReadNotFound(string id) {
		ArgumentException.ThrowIfNullOrEmpty(id);
		return Build(writer => {
			writer.WriteString("type", "fs-read-result");
			writer.WriteString("id", id);
			writer.WriteBoolean("ok", false);
			writer.WriteString("code", "FileNotFound");
		});
	}

	/// <summary>
	/// Reply to <c>fs-read</c> for a genuine read failure. No <c>FileNotFound</c> code, so the provider raises an
	/// Unknown error that propagates loudly rather than silently falling through — a real failure stays observable.
	/// </summary>
	public static string ReadError(string id, string error) {
		ArgumentException.ThrowIfNullOrEmpty(id);
		ArgumentNullException.ThrowIfNull(error);
		return Build(writer => {
			writer.WriteString("type", "fs-read-result");
			writer.WriteString("id", id);
			writer.WriteBoolean("ok", false);
			writer.WriteString("error", error);
		});
	}

	/// <summary>Reply to <c>fs-write</c> with the post-write stat so the provider updates its etag without re-statting.</summary>
	public static string WriteResult(string id, FileStat stat) {
		ArgumentException.ThrowIfNullOrEmpty(id);
		return Build(writer => {
			writer.WriteString("type", "fs-write-result");
			writer.WriteString("id", id);
			writer.WriteBoolean("ok", true);
			writer.WriteNumber("mtimeMs", stat.MtimeMs);
			writer.WriteNumber("size", stat.Size);
		});
	}

	/// <summary>Reply to <c>fs-write</c> for a failed write (out-of-workspace or IO error); the provider rejects the save.</summary>
	public static string WriteError(string id, string error) {
		ArgumentException.ThrowIfNullOrEmpty(id);
		ArgumentNullException.ThrowIfNull(error);
		return Build(writer => {
			writer.WriteString("type", "fs-write-result");
			writer.WriteString("id", id);
			writer.WriteBoolean("ok", false);
			writer.WriteString("error", error);
		});
	}

	/// <summary>An <c>fs-change</c> push: tells the provider that files changed on disk so it fires its change event.</summary>
	/// <param name="changes">The batch of changed paths + kinds.</param>
	public static string Changes(IReadOnlyList<FileProviderChange> changes) {
		ArgumentNullException.ThrowIfNull(changes);
		return Build(writer => {
			writer.WriteString("type", "fs-change");
			writer.WriteStartArray("changes");
			foreach (var change in changes) {
				writer.WriteStartObject();
				writer.WriteString("path", change.Path);
				writer.WriteString("kind", change.Kind);
				writer.WriteEndObject();
			}

			writer.WriteEndArray();
		});
	}

	/// <summary>A single-path <c>fs-change</c> push (the common case: one file just changed).</summary>
	/// <param name="path">The changed file's native path.</param>
	/// <param name="kind"><c>"added"</c>, <c>"updated"</c>, or <c>"deleted"</c>.</param>
	public static string Changed(string path, string kind) =>
		Changes([new FileProviderChange(path, kind)]);

	/// <summary>
	/// Builds an <c>fs-change</c> push from a workspace-watcher batch (URIs → native paths, kinds → web kinds),
	/// forwarding non-Claude on-disk edits to the provider. Returns <see langword="null"/> when nothing maps.
	/// </summary>
	/// <param name="changes">The watcher's debounced change batch.</param>
	public static string? WatchedChanges(IReadOnlyList<WatchedFileChange> changes) {
		ArgumentNullException.ThrowIfNull(changes);
		var mapped = new List<FileProviderChange>(changes.Count);
		foreach (var change in changes) {
			if (TryToLocalPath(change.Uri, out string path)) {
				mapped.Add(new FileProviderChange(path, MapKind(change.Kind)));
			}
		}

		return mapped.Count > 0 ? Changes(mapped) : null;
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

	private static string Build(Action<Utf8JsonWriter> body) {
		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream)) {
			writer.WriteStartObject();
			body(writer);
			writer.WriteEndObject();
		}

		return Encoding.UTF8.GetString(stream.ToArray());
	}
}
