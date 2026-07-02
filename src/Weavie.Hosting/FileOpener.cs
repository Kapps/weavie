using System.Text;
using System.Text.Json;
using Weavie.Core.Editor;

namespace Weavie.Hosting;

/// <summary>
/// Pushes a file open to the web to reveal at a line. Shared by clickable terminal file:line links and the
/// MCP <c>openFile</c> tool; relative paths resolve against the workspace. The gate goes through the
/// session's <see cref="FileProviderService"/>, the one validated reader, so an open is confined to the
/// worktree (+ scratch) by normalized path. (The opened repo is trusted: an in-tree symlink that resolves
/// outside is still followed — confinement is by path string, not by the link target.)
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
	/// Pushes an <c>open-file</c> so the web opens the file (Monaco working copy, or the media pane for
	/// images/video) and reveals the 1-based line (logs and returns if missing). No content rides along —
	/// the web reads disk through the fs provider. <paramref name="preview"/> opens a reusable preview tab;
	/// <paramref name="scratch"/> marks an untitled buffer shown as "Untitled-N".
	/// </summary>
	public void Open(string path, int line, bool preview, bool scratch) {
		string resolved = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(Workspace, path));
		// Validated gate: refused for a path outside the worktree (+ scratch) or missing, so a reveal-file /
		// openFile is confined to the worktree by path (an in-tree symlink is followed — the repo is trusted).
		if (!_files.CanRead(resolved)) {
			Console.Error.WriteLine($"[weavie] reveal-file: refused or not found: {resolved}");
			return;
		}

		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream)) {
			writer.WriteStartObject();
			writer.WriteString("type", "open-file");
			writer.WriteString("path", resolved);
			writer.WriteNumber("line", Math.Max(1, line));
			writer.WriteBoolean("preview", preview);
			writer.WriteBoolean("scratch", scratch);
			writer.WriteEndObject();
		}

		_channel.Reveal(Encoding.UTF8.GetString(stream.ToArray()));
	}
}
