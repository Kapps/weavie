using System.Text;
using System.Text.Json;
using Weavie.Core.FileSystem;

namespace Weavie.Hosting;

/// <summary>
/// Loads a file from disk and pushes its contents to Monaco to reveal at a line. Shared by clickable terminal
/// file:line links and the MCP <c>openFile</c> tool; relative paths resolve against the workspace.
/// </summary>
public sealed class FileOpener {
	private readonly SessionEditorChannel _channel;
	private readonly IFileSystem _fileSystem;

	/// <summary>Pushes files to Monaco through the session's editor <paramref name="channel"/> (so a muted session's opens are held, not posted into the foreground); relative paths resolve against <paramref name="workspace"/>.</summary>
	public FileOpener(SessionEditorChannel channel, IFileSystem fileSystem, string workspace) {
		ArgumentNullException.ThrowIfNull(channel);
		ArgumentNullException.ThrowIfNull(fileSystem);
		_channel = channel;
		_fileSystem = fileSystem;
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
		if (!_fileSystem.FileExists(resolved)) {
			Console.Error.WriteLine($"[weavie] reveal-file: not found: {resolved}");
			return;
		}

		string content = _fileSystem.ReadAllText(resolved);
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
