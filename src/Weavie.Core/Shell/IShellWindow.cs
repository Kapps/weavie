namespace Weavie.Core.Shell;

/// <summary>
/// The OS-specific window primitives the web title bar drives — the only part of the custom-chrome flow
/// that can't live in Core, because minimizing/maximizing a window and showing a folder picker are
/// platform calls (WinForms, Cocoa). A host's workspace window implements this; <see cref="ShellController"/>
/// parses the web's title-bar messages and calls these. Keep it tiny: everything else (message parse/build,
/// the file index) stays in Core and is shared across platforms.
/// </summary>
public interface IShellWindow {
	/// <summary>Minimizes this window.</summary>
	void Minimize();

	/// <summary>Toggles this window between maximized and restored.</summary>
	void ToggleMaximize();

	/// <summary>
	/// Begins an interactive resize of this window from <paramref name="edge"/>, following the cursor until the
	/// button is released. Used by the frameless custom chrome: the WebView covers the host's real resize
	/// border, so the web draws grab handles at the edges and calls this to hand off to the OS's native resize.
	/// </summary>
	void StartResize(ResizeEdge edge);

	/// <summary>Closes this window — the title-bar ✕ button. The host decides last-window behavior.</summary>
	void Close();

	/// <summary>
	/// Closes this window via the File ▸ Close Window menu item. Distinct from <see cref="Close"/> so a host
	/// can give the menu different last-window behavior (e.g. fall back to a welcome window) than the ✕.
	/// </summary>
	void CloseWindow();

	/// <summary>Quits the whole app (the File ▸ Exit action).</summary>
	void Quit();

	/// <summary>Shows the native "open folder as workspace" picker.</summary>
	void ShowOpenFolderPicker();

	/// <summary>Opens (or focuses) the workspace at <paramref name="path"/> — an Open Recent selection.</summary>
	void OpenWorkspace(string path);
}
