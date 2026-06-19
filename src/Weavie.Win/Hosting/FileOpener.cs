using System.Text;
using System.Text.Json;
using Weavie.Core.FileSystem;
using Weavie.Core.Workspaces;

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
	/// contents to the Monaco editor, revealing line <paramref name="line"/>. Opens a reusable preview tab
	/// when <paramref name="preview"/> is set; otherwise a persistent tab. <paramref name="scratch"/> marks an
	/// untitled buffer (a fresh New File, or a restored scratch) so the editor shows it as "Untitled-N".
	/// </summary>
	public void Open(string path, int line, bool preview, bool scratch) {
		string resolved = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(Workspace, path));
		if (!_fileSystem.FileExists(resolved)) {
			Console.Error.WriteLine($"[weavie] reveal-file: not found: {resolved}");
			return;
		}

		string content = _fileSystem.ReadAllText(resolved);
		// Hand the editor the canonical (lowercase-drive) spelling it keys working copies / tabs by, so a file
		// already open under Monaco's `fsPath` spelling is reused instead of opening a second copy — this is the
		// single `open-file` chokepoint, so it covers reveal-file (omnibar / file browser / terminal links), the
		// MCP openFile tool, and scratch buffers alike. See WorkspacePaths.CanonicalFsPath / editor/fs-path.ts.
		string canonical = WorkspacePaths.CanonicalFsPath(resolved);
		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream)) {
			writer.WriteStartObject();
			writer.WriteString("type", "open-file");
			writer.WriteString("path", canonical);
			writer.WriteString("content", content);
			writer.WriteNumber("line", Math.Max(1, line));
			writer.WriteBoolean("preview", preview);
			writer.WriteBoolean("scratch", scratch);
			writer.WriteEndObject();
		}

		_bridge.PostToWeb(Encoding.UTF8.GetString(stream.ToArray()));
	}
}
