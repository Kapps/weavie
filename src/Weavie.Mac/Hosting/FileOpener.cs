using System.Text;
using System.Text.Json;
using Weavie.Core.FileSystem;

namespace Weavie.Mac.Hosting;

/// <summary>
/// Loads a file from disk and pushes its contents to the Monaco editor to reveal at a line.
/// Shared by clickable terminal file:line links and the MCP <c>openFile</c> tool. Relative paths
/// resolve against the workspace.
/// </summary>
public sealed class FileOpener {
	private readonly HostBridge _bridge;
	private readonly IFileSystem _fileSystem;

	/// <summary>Creates an opener that pushes files to Monaco via the bridge, resolving relative paths against <paramref name="workspace"/>.</summary>
	public FileOpener(HostBridge bridge, IFileSystem fileSystem, string workspace) {
		ArgumentNullException.ThrowIfNull(bridge);
		ArgumentNullException.ThrowIfNull(fileSystem);
		_bridge = bridge;
		_fileSystem = fileSystem;
		Workspace = workspace;
	}

	/// <summary>The directory relative paths are resolved against.</summary>
	public string Workspace { get; set; }

	/// <summary>
	/// Reads the file (relative paths resolve against <see cref="Workspace"/>) and pushes an
	/// <c>open-file</c> message so Monaco loads it and reveals the given 1-based line; logs and
	/// returns if the file does not exist. Opens a reusable preview tab when <paramref name="preview"/>
	/// is set; otherwise a persistent tab. <paramref name="scratch"/> marks an untitled buffer (a fresh
	/// New File, or a restored scratch) so the editor shows it as "Untitled-N".
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

		_bridge.PostToWeb(Encoding.UTF8.GetString(stream.ToArray()));
	}
}
