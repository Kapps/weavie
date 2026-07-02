using System.Text;
using System.Text.Json;
using Weavie.Core.Editor;
using Weavie.Core.Shell;

namespace Weavie.Hosting;

/// <summary>
/// Loads a file and pushes its contents to Monaco to reveal at a line. Shared by clickable terminal file:line
/// links and the MCP <c>openFile</c> tool; relative paths resolve against the workspace. The read goes through
/// the session's <see cref="FileProviderService"/>, the one validated reader, so an open is confined to the
/// worktree (+ scratch) by normalized path. (The opened repo is trusted: an in-tree symlink that resolves
/// outside is still followed — confinement is by path string, not by the link target.)
/// </summary>
public sealed class FileOpener {
	private readonly SessionEditorChannel _channel;
	private readonly FileProviderService _files;
	private readonly IHostBridge _bridge;

	/// <summary>Pushes files to Monaco through the session's editor <paramref name="channel"/> (so a muted session's opens are held, not posted into the foreground), reading through <paramref name="files"/> (the validated, workspace-confined reader); relative paths resolve against <paramref name="workspace"/>. A refused open toasts through <paramref name="bridge"/>.</summary>
	public FileOpener(SessionEditorChannel channel, FileProviderService files, IHostBridge bridge, string workspace) {
		ArgumentNullException.ThrowIfNull(channel);
		ArgumentNullException.ThrowIfNull(files);
		ArgumentNullException.ThrowIfNull(bridge);
		_channel = channel;
		_files = files;
		_bridge = bridge;
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
		// Validated read: null for a path outside the worktree (+ scratch), missing, or unreadable, so a
		// reveal-file / openFile is confined to the worktree by path (an in-tree symlink is followed — the repo is
		// trusted). A refusal toasts — the user clicked something (an omnibar row, a terminal link) and a silent
		// drop reads as the app ignoring them.
		if (_files.ReadIfAllowed(resolved) is not { } content) {
			Console.Error.WriteLine($"[weavie] reveal-file: refused or not found: {resolved}");
			_bridge.PostToWeb(ShellProtocol.BuildNotify(
				"warn", $"Couldn't open {Path.GetFileName(resolved)} — it's missing, unreadable, or outside this session's worktree."));
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
