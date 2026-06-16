using System.Text;
using System.Text.Json;
using Weavie.Core.FileSystem;

namespace Weavie.Win.Hosting;

/// <summary>
/// Loads a file from disk and pushes its contents to the Monaco editor to reveal at a line.
/// Shared by clickable terminal file:line links and the MCP <c>openFile</c> tool. Relative paths
/// resolve against the workspace.
/// </summary>
public sealed class FileOpener {
	private readonly HostBridge _bridge;
	private readonly IFileSystem _fileSystem;

	/// <summary>
	/// Creates a file opener that reveals files in the Monaco editor over the bridge; relative
	/// paths are resolved against <paramref name="workspace"/>.
	/// </summary>
	public FileOpener(HostBridge bridge, IFileSystem fileSystem, string workspace) {
		ArgumentNullException.ThrowIfNull(bridge);
		ArgumentNullException.ThrowIfNull(fileSystem);
		_bridge = bridge;
		_fileSystem = fileSystem;
		Workspace = workspace;
	}

	/// <summary>The directory that relative paths passed to <see cref="Open"/> resolve against.</summary>
	public string Workspace { get; set; }

	/// <summary>
	/// Loads <paramref name="path"/> (relative paths resolve against the workspace) and pushes its
	/// contents to the Monaco editor, revealing line <paramref name="line"/>.
	/// </summary>
	public void Open(string path, int line) {
		var resolved = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(Workspace, path));
		if (!_fileSystem.FileExists(resolved)) {
			Console.Error.WriteLine($"[weavie] reveal-file: not found: {resolved}");
			return;
		}

		var content = _fileSystem.ReadAllText(resolved);
		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream)) {
			writer.WriteStartObject();
			writer.WriteString("type", "open-file");
			writer.WriteString("path", resolved);
			writer.WriteString("content", content);
			writer.WriteNumber("line", Math.Max(1, line));
			writer.WriteEndObject();
		}

		_bridge.PostToWeb(Encoding.UTF8.GetString(stream.ToArray()));
	}
}
