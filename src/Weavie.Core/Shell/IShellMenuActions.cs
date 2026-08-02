namespace Weavie.Core.Shell;

/// <summary>The native application/workspace actions exposed by a web-rendered File menu.</summary>
public interface IShellMenuActions {
	/// <summary>Closes the current workspace window.</summary>
	void CloseWindow();

	/// <summary>Quits the application.</summary>
	void Quit();

	/// <summary>Shows the native folder picker and opens the selected workspace.</summary>
	void ShowOpenFolderPicker();

	/// <summary>Opens the workspace at <paramref name="path"/>.</summary>
	void OpenWorkspace(string path);
}

/// <summary>Explicit menu-action implementation for hosts that render no web application menu.</summary>
public sealed class NoopShellMenuActions : IShellMenuActions {
	private NoopShellMenuActions() { }

	/// <summary>The shared no-op instance.</summary>
	public static NoopShellMenuActions Instance { get; } = new();

	/// <inheritdoc/>
	public void CloseWindow() { }

	/// <inheritdoc/>
	public void Quit() { }

	/// <inheritdoc/>
	public void ShowOpenFolderPicker() { }

	/// <inheritdoc/>
	public void OpenWorkspace(string path) { }
}
