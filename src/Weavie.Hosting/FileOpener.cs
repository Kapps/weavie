using System.Text;
using System.Text.Json;
using Weavie.Core.Editor;

namespace Weavie.Hosting;

/// <summary>
/// Loads a file and pushes its contents to Monaco to reveal at a line. Shared by clickable terminal file:line
/// links and the MCP <c>openFile</c> tool; relative paths resolve against the workspace. The read goes through
/// the session's <see cref="FileProviderService"/>, the one validated reader, so an open is confined to the
/// worktree (+ scratch) — a terminal link or MCP call can't reveal an arbitrary path.
/// </summary>
public sealed class FileOpener {
	private readonly SessionEditorChannel _channel;
	private readonly FileProviderService _files;

	/// <summary>Pushes files to Monaco through the session's editor <paramref name="channel"/> (so a muted session's opens are held, not posted into the foreground), reading through <paramref name="files"/> (the validated, workspace-confined reader); relative paths resolve against <paramref name="workspace"/>.</summary>
	public FileOpener(SessionEditorChannel channel, FileProviderService files, string workspace) {
		ArgumentNullException.ThrowIfNull(channel);
		ArgumentNullException.ThrowIfNull(files);
		_channel = channel;
		_files = files;
		Workspace = workspace;
	}

	/// <summary>The directory relative paths are resolved against.</summary>
	public string Workspace { get; set; }

	/// <summary>
	/// Reads the file and pushes an <c>open-file</c> so Monaco loads it and reveals the 1-based line (logs and
	/// returns if missing). <paramref name="preview"/> opens a reusable preview tab; <paramref name="scratch"/>
	/// marks an untitled buffer shown as "Untitled-N".
	/// </summary>
	public void Open(string path, int line, bool preview, bool scratch) {
		string resolved = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(Workspace, path));
		// Validated read: null for an out-of-workspace, missing, or unreadable path, so a reveal-file / openFile
		// can't be coaxed into reading an arbitrary file off a terminal link or MCP call.
		if (_files.ReadIfAllowed(resolved) is not { } content) {
			Console.Error.WriteLine($"[weavie] reveal-file: refused or not found: {resolved}");
			return;
		}

		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream)) {
			writer.WriteStartObject();
			writer.WriteString("type", "open-file");
			writer.WriteString("path", resolved);
			writer.WriteString("content", content);
			writer.WriteNumber("line", Math.Max(1, line));
			writer.WriteBoolean("preview", preview);
			writer.WriteBoolean("scratch", scratch);
			writer.WriteEndObject();
		}

		_channel.Reveal(Encoding.UTF8.GetString(stream.ToArray()));
	}
}
