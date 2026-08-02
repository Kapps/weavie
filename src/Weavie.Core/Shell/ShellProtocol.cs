using System.Text.Json;

namespace Weavie.Core.Shell;

/// <summary>A window-control button the web title bar offers.</summary>
public enum WindowControl {
	/// <summary>Minimize the window.</summary>
	Minimize,

	/// <summary>Toggle maximized/restored.</summary>
	MaximizeToggle,

	/// <summary>Close the window.</summary>
	Close,
}

/// <summary>
/// A window edge or corner grabbed to resize the frameless window. The web names the edge; the host maps
/// it to the matching <c>HT*</c> code to begin a native resize. See <c>ResizeFrame.tsx</c> + <c>CustomChrome</c>.
/// </summary>
public enum ResizeEdge {
	/// <summary>The left edge.</summary>
	Left,

	/// <summary>The right edge.</summary>
	Right,

	/// <summary>The top edge.</summary>
	Top,

	/// <summary>The bottom edge.</summary>
	Bottom,

	/// <summary>The top-left corner.</summary>
	TopLeft,

	/// <summary>The top-right corner.</summary>
	TopRight,

	/// <summary>The bottom-left corner.</summary>
	BottomLeft,

	/// <summary>The bottom-right corner.</summary>
	BottomRight,
}

/// <summary>A File-menu command the web title bar offers.</summary>
public enum MenuCommand {
	/// <summary>Show the open-folder picker.</summary>
	OpenFolder,

	/// <summary>Open a recent workspace (carries a <c>path</c>).</summary>
	OpenRecent,

	/// <summary>Close the current window.</summary>
	CloseWindow,

	/// <summary>Quit the app.</summary>
	Exit,
}

/// <summary>
/// Builds the boot-time title-bar configuration and parses window feature payloads shared by every host.
/// </summary>
public static class ShellProtocol {
	/// <summary>
	/// Builds the <c>window.__WEAVIE_SHELL__ = {…};</c> script the host injects before navigation: which
	/// platform/title-bar to render, the workspace label, and the recents for File ▸ Open Recent.
	/// </summary>
	/// <param name="platform">Short platform id, e.g. <c>win</c> or <c>mac</c>.</param>
	/// <param name="titleBar">Title-bar mode (<c>custom</c>, <c>mac</c>, or <c>linux</c>), or null.</param>
	/// <param name="workspaceLabel">The window's workspace label (folder leaf name).</param>
	/// <param name="recents">Recent workspace paths (absolute); the web derives leaf names for display.</param>
	/// <param name="buildNumber">The app's build identity (SemVer with the build number as patch), shown read-only in the title bar.</param>
	public static string BuildConfigScript(
		string platform,
		string? titleBar,
		string workspaceLabel,
		IReadOnlyList<string> recents,
		string buildNumber) {
		ArgumentException.ThrowIfNullOrEmpty(platform);
		ArgumentNullException.ThrowIfNull(workspaceLabel);
		ArgumentNullException.ThrowIfNull(recents);
		ArgumentException.ThrowIfNullOrEmpty(buildNumber);
		string json = JsonSerializer.Serialize(new {
			platform,
			titleBar,
			workspaceLabel,
			recents,
			buildNumber,
		});
		return $"window.__WEAVIE_SHELL__ = {json};";
	}

	/// <summary>Parses a <c>window.control</c> payload's <c>action</c>. False for an unknown/missing action.</summary>
	public static bool TryParseWindowControl(JsonElement message, out WindowControl control) {
		control = default;
		string? action = message.TryGetProperty("action", out var a) ? a.GetString() : null;
		switch (action) {
			case "minimize":
				control = WindowControl.Minimize;
				return true;
			case "maximize-toggle":
				control = WindowControl.MaximizeToggle;
				return true;
			case "close":
				control = WindowControl.Close;
				return true;
			default:
				return false;
		}
	}

	/// <summary>Parses a <c>window.resize</c> payload's <c>edge</c>. False for an unknown/missing edge.</summary>
	public static bool TryParseWindowResize(JsonElement message, out ResizeEdge edge) {
		edge = default;
		string? value = message.TryGetProperty("edge", out var e) ? e.GetString() : null;
		switch (value) {
			case "left":
				edge = ResizeEdge.Left;
				return true;
			case "right":
				edge = ResizeEdge.Right;
				return true;
			case "top":
				edge = ResizeEdge.Top;
				return true;
			case "bottom":
				edge = ResizeEdge.Bottom;
				return true;
			case "top-left":
				edge = ResizeEdge.TopLeft;
				return true;
			case "top-right":
				edge = ResizeEdge.TopRight;
				return true;
			case "bottom-left":
				edge = ResizeEdge.BottomLeft;
				return true;
			case "bottom-right":
				edge = ResizeEdge.BottomRight;
				return true;
			default:
				return false;
		}
	}

	/// <summary>
	/// Parses a <c>window.menu</c> payload's <c>action</c> (and optional <c>path</c> for Open Recent). False for
	/// an unknown/missing action.
	/// </summary>
	public static bool TryParseMenuAction(JsonElement message, out MenuCommand command, out string? path) {
		command = default;
		path = message.TryGetProperty("path", out var p) ? p.GetString() : null;
		string? action = message.TryGetProperty("action", out var a) ? a.GetString() : null;
		switch (action) {
			case "open-folder":
				command = MenuCommand.OpenFolder;
				return true;
			case "open-recent":
				command = MenuCommand.OpenRecent;
				return true;
			case "close-window":
				command = MenuCommand.CloseWindow;
				return true;
			case "exit":
				command = MenuCommand.Exit;
				return true;
			default:
				return false;
		}
	}
}
