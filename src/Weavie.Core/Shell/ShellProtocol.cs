using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weavie.Core.Shell;

/// <summary>A window button the page's title bar draws; its wire name is the <c>window.control</c> action.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WindowControl>))]
public enum WindowControl {
	/// <summary>Minimize the window.</summary>
	[JsonStringEnumMemberName("minimize")]
	Minimize,

	/// <summary>Toggle maximized/restored.</summary>
	[JsonStringEnumMemberName("maximize-toggle")]
	MaximizeToggle,

	/// <summary>Close the window.</summary>
	[JsonStringEnumMemberName("close")]
	Close,
}

/// <summary>
/// A window edge or corner grabbed to resize the frameless window. The web names the edge; the host maps
/// it to the matching <c>HT*</c> code to begin a native resize. See <c>ResizeFrame.tsx</c> + <c>CustomChrome</c>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ResizeEdge>))]
public enum ResizeEdge {
	/// <summary>The left edge.</summary>
	[JsonStringEnumMemberName("left")]
	Left,

	/// <summary>The right edge.</summary>
	[JsonStringEnumMemberName("right")]
	Right,

	/// <summary>The top edge.</summary>
	[JsonStringEnumMemberName("top")]
	Top,

	/// <summary>The bottom edge.</summary>
	[JsonStringEnumMemberName("bottom")]
	Bottom,

	/// <summary>The top-left corner.</summary>
	[JsonStringEnumMemberName("top-left")]
	TopLeft,

	/// <summary>The top-right corner.</summary>
	[JsonStringEnumMemberName("top-right")]
	TopRight,

	/// <summary>The bottom-left corner.</summary>
	[JsonStringEnumMemberName("bottom-left")]
	BottomLeft,

	/// <summary>The bottom-right corner.</summary>
	[JsonStringEnumMemberName("bottom-right")]
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
/// Builds the boot-time title-bar configuration and parses the File-menu payloads shared by every host.
/// </summary>
public static class ShellProtocol {
	/// <summary>
	/// Builds the <c>window.__WEAVIE_SHELL__ = {…};</c> script the host injects before navigation: which
	/// platform, the window chrome to draw, the workspace label, and the recents for File ▸ Open Recent.
	/// </summary>
	/// <param name="platform">Short platform id, e.g. <c>win</c> or <c>mac</c>.</param>
	/// <param name="chrome">What the page's title bar draws for this window.</param>
	/// <param name="workspaceLabel">The window's workspace label (folder leaf name).</param>
	/// <param name="recents">Recent workspace paths (absolute); the web derives leaf names for display.</param>
	/// <param name="buildNumber">The app's build identity (SemVer with the build number as patch), shown read-only in the title bar.</param>
	public static string BuildConfigScript(
		string platform,
		WindowChrome chrome,
		string workspaceLabel,
		IReadOnlyList<string> recents,
		string buildNumber) {
		ArgumentException.ThrowIfNullOrEmpty(platform);
		ArgumentNullException.ThrowIfNull(chrome);
		ArgumentNullException.ThrowIfNull(workspaceLabel);
		ArgumentNullException.ThrowIfNull(recents);
		ArgumentException.ThrowIfNullOrEmpty(buildNumber);
		string json = JsonSerializer.Serialize(
			new ShellConfigWire(platform, chrome, workspaceLabel, recents, buildNumber),
			PageJson.Default.ShellConfigWire);
		return $"window.__WEAVIE_SHELL__ = {json};";
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

internal sealed record ShellConfigWire(
	string Platform,
	WindowChrome Chrome,
	string WorkspaceLabel,
	IReadOnlyList<string> Recents,
	string BuildNumber);
