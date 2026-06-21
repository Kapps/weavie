using System.Text.Json;
using Weavie.Core.Workspaces;

namespace Weavie.Core.Shell;

/// <summary>
/// Orchestrates the custom title bar's live message flow between the web shell and one host window. The
/// host routes title-bar messages (<c>window-control</c>, <c>menu-action</c>, <c>request-file-index</c>)
/// here; this parses them via <see cref="ShellProtocol"/>, drives the platform primitives
/// (<see cref="IShellWindow"/>) and the workspace file index, and pushes replies back over
/// <c>postToWeb</c>. OS-agnostic, so both hosts share it — the host supplies only the
/// <see cref="IShellWindow"/> and its bridge's post delegate.
/// </summary>
public sealed class ShellController {
	private readonly IShellWindow _window;
	private readonly WorkspaceFileIndex _fileIndex;
	private readonly Action<string> _postToWeb;

	/// <summary>Creates a controller driving <paramref name="window"/>, indexing via <paramref name="fileIndex"/>, replying over <paramref name="postToWeb"/>.</summary>
	public ShellController(IShellWindow window, WorkspaceFileIndex fileIndex, Action<string> postToWeb) {
		ArgumentNullException.ThrowIfNull(window);
		ArgumentNullException.ThrowIfNull(fileIndex);
		ArgumentNullException.ThrowIfNull(postToWeb);
		_window = window;
		_fileIndex = fileIndex;
		_postToWeb = postToWeb;
	}

	/// <summary>Handles a <c>window-control</c> message: minimize / toggle-maximize / close.</summary>
	public void HandleWindowControl(JsonElement message) {
		if (!ShellProtocol.TryParseWindowControl(message, out var control)) {
			return;
		}

		switch (control) {
			case WindowControl.Minimize:
				_window.Minimize();
				break;
			case WindowControl.MaximizeToggle:
				_window.ToggleMaximize();
				break;
			case WindowControl.Close:
				_window.Close();
				break;
		}
	}

	/// <summary>Handles a <c>window-resize</c> message: begins a native resize from the named edge/corner.</summary>
	public void HandleWindowResize(JsonElement message) {
		if (ShellProtocol.TryParseWindowResize(message, out var edge)) {
			_window.StartResize(edge);
		}
	}

	/// <summary>Handles a <c>menu-action</c> message: open folder / open recent / close window / exit.</summary>
	public void HandleMenuAction(JsonElement message) {
		if (!ShellProtocol.TryParseMenuAction(message, out var command, out string? path)) {
			return;
		}

		switch (command) {
			case MenuCommand.OpenFolder:
				_window.ShowOpenFolderPicker();
				break;
			case MenuCommand.OpenRecent:
				if (!string.IsNullOrEmpty(path)) {
					_window.OpenWorkspace(path);
				}

				break;
			case MenuCommand.CloseWindow:
				_window.CloseWindow();
				break;
			case MenuCommand.Exit:
				_window.Quit();
				break;
		}
	}

	/// <summary>Walks the workspace and pushes the <c>file-index</c> reply for the omnibar quick-open.</summary>
	public void PushFileIndex() => _postToWeb(ShellProtocol.BuildFileIndex(_fileIndex.Root, _fileIndex.List(WorkspaceFileIndex.DefaultCap)));

	/// <summary>Pushes the current window state (maximized + focused) so the title bar updates its glyph and dim.</summary>
	public void PushWindowState(bool maximized, bool focused) => _postToWeb(ShellProtocol.BuildWindowState(maximized, focused));
}
