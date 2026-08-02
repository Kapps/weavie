namespace Weavie.Core.Shell;

/// <summary>
/// The OS-specific window primitives the web title bar drives (WinForms, Cocoa), implemented by a host's
/// workspace window. <see cref="ShellController"/> parses the web's title-bar messages and calls these.
/// </summary>
public interface IShellWindow {
	/// <summary>Minimizes this window.</summary>
	void Minimize();

	/// <summary>Toggles this window between maximized and restored.</summary>
	void ToggleMaximize();

	/// <summary>
	/// Begins an interactive native resize from <paramref name="edge"/>. The frameless WebView covers the
	/// host's real resize border, so the web draws grab handles and hands off to the OS here.
	/// </summary>
	void StartResize(ResizeEdge edge);

	/// <summary>Closes this window — the title-bar ✕ button. The host decides last-window behavior.</summary>
	void Close();

}
