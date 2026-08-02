using System.Text.Json;

namespace Weavie.Core.Shell;

/// <summary>Parses web File-menu actions and drives the platform application/workspace adapter.</summary>
public sealed class ShellMenuController {
	private readonly IShellMenuActions _actions;

	/// <summary>Creates a controller driving <paramref name="actions"/>.</summary>
	public ShellMenuController(IShellMenuActions actions) {
		ArgumentNullException.ThrowIfNull(actions);
		_actions = actions;
	}

	/// <summary>Handles a <c>window.menu</c> payload: open folder / open recent / close window / exit.</summary>
	public void HandleMenuAction(JsonElement message) {
		if (!ShellProtocol.TryParseMenuAction(message, out var command, out string? path)) {
			return;
		}

		switch (command) {
			case MenuCommand.OpenFolder:
				_actions.ShowOpenFolderPicker();
				break;
			case MenuCommand.OpenRecent:
				if (!string.IsNullOrEmpty(path)) {
					_actions.OpenWorkspace(path);
				}

				break;
			case MenuCommand.CloseWindow:
				_actions.CloseWindow();
				break;
			case MenuCommand.Exit:
				_actions.Quit();
				break;
		}
	}
}
