using System.Text.Json;

namespace Weavie.Core.Shell;

/// <summary>
/// Parses custom-title-bar actions and drives one platform window. Outbound window state belongs to the
/// host message bus rather than this platform adapter.
/// </summary>
public sealed class ShellController {
	private readonly IShellWindow _window;

	/// <summary>Creates a controller driving <paramref name="window"/>.</summary>
	public ShellController(IShellWindow window) {
		ArgumentNullException.ThrowIfNull(window);
		_window = window;
	}

	/// <summary>Handles a <c>window.control</c> payload: minimize / toggle-maximize / close.</summary>
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

	/// <summary>Handles a <c>window.resize</c> payload: begins a native resize from the named edge/corner.</summary>
	public void HandleWindowResize(JsonElement message) {
		if (ShellProtocol.TryParseWindowResize(message, out var edge)) {
			_window.StartResize(edge);
		}
	}

}
