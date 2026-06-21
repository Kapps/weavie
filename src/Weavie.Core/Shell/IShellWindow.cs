namespace Weavie.Core.Shell;

/// <summary>
/// The OS-specific window primitives the web title bar drives — the part of the custom-chrome flow that
/// needs platform calls (WinForms, Cocoa). A host's workspace window implements this;
/// <see cref="ShellController"/> parses the web's title-bar messages and calls these. Keep it tiny:
/// message parse/build and the file index stay in Core, shared across platforms.
/// </summary>
public interface IShellWindow {
	/// <summary>Minimizes this window.</summary>
	void Minimize();

	/// <summary>Toggles this window between maximized and restored.</summary>
	void ToggleMaximize();

	/// <summary>
	/// Begins an interactive native resize from <paramref name="edge"/>, following the cursor. The frameless
	/// WebView covers the host's real resize border, so the web draws grab handles and hands off to the OS here.
	/// </summary>
	void StartResize(ResizeEdge edge);

	/// <summary>Closes this window — the title-bar ✕ button. The host decides last-window behavior.</summary>
	void Close();

	/// <summary>
	/// Closes this window via File ▸ Close Window. Distinct from <see cref="Close"/> so a host can give the
	/// menu different last-window behavior (e.g. fall back to a welcome window) than the ✕.
	/// </summary>
	void CloseWindow();

	/// <summary>Quits the whole app (the File ▸ Exit action).</summary>
	void Quit();

	/// <summary>Shows the native "open folder as workspace" picker.</summary>
	void ShowOpenFolderPicker();

	/// <summary>Opens (or focuses) the workspace at <paramref name="path"/> — an Open Recent selection.</summary>
	void OpenWorkspace(string path);
}
