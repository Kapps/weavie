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
/// Builds and parses the title-bar bridge messages exchanged with the web shell — one shared wire contract
/// both hosts use. Mirrors the message <c>type</c>s in the web's <c>bridge.ts</c>.
/// <list type="bullet">
/// <item>host → web: the <c>__WEAVIE_SHELL__</c> config script, <c>window-state</c>, <c>file-index</c>.</item>
/// <item>web → host: <c>window-control</c>, <c>menu-action</c> (parsed into the enums above).</item>
/// </list>
/// </summary>
public static class ShellProtocol {
	/// <summary>
	/// Builds the <c>window.__WEAVIE_SHELL__ = {…};</c> script the host injects before navigation: which
	/// platform/title-bar to render, the workspace label, and the recents for File ▸ Open Recent.
	/// </summary>
	/// <param name="platform">Short platform id, e.g. <c>win</c> or <c>mac</c>.</param>
	/// <param name="titleBar">Title-bar mode (<c>custom</c>), or null for native chrome.</param>
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

	/// <summary>Builds the <c>notify</c> message (a user-facing toast in the page).</summary>
	public static string BuildNotify(string level, string message) {
		ArgumentException.ThrowIfNullOrEmpty(level);
		ArgumentNullException.ThrowIfNull(message);
		return JsonSerializer.Serialize(new { type = "notify", level, message });
	}

	/// <summary>
	/// As <see cref="BuildNotify(string,string)"/>, with a dedupe <paramref name="key"/>: a later toast carrying
	/// the same key replaces the live one in place.
	/// </summary>
	public static string BuildNotify(string level, string message, string key) {
		ArgumentException.ThrowIfNullOrEmpty(level);
		ArgumentNullException.ThrowIfNull(message);
		ArgumentException.ThrowIfNullOrEmpty(key);
		return JsonSerializer.Serialize(new { type = "notify", level, message, key });
	}

	/// <summary>Builds the <c>notify-clear</c> message: dismisses the live toast carrying <paramref name="key"/> (e.g. a resolved in-flight spinner).</summary>
	public static string BuildNotifyClear(string key) {
		ArgumentException.ThrowIfNullOrEmpty(key);
		return JsonSerializer.Serialize(new { type = "notify-clear", key });
	}

	/// <summary>Builds the <c>window-state</c> message (maximize glyph + blur dim) the host pushes on focus/size changes.</summary>
	public static string BuildWindowState(bool maximized, bool focused) =>
		JsonSerializer.Serialize(new { type = "window-state", maximized, focused });

	/// <summary>Builds an owned <c>file-index</c> reply for the omnibar quick-open.</summary>
	public static string BuildFileIndex(string root, IReadOnlyList<string> files, string railSessionId) {
		ArgumentException.ThrowIfNullOrEmpty(root);
		ArgumentNullException.ThrowIfNull(files);
		ArgumentException.ThrowIfNullOrEmpty(railSessionId);
		return JsonSerializer.Serialize(new { type = "file-index", root, files, railSessionId });
	}

	/// <summary>
	/// Builds a projection's owned root announcement. A changed owner/root replaces stale files; the same owner
	/// may keep its valid cache while an explicit refresh runs.
	/// </summary>
	public static string BuildFileIndexPending(string root, string railSessionId) {
		ArgumentException.ThrowIfNullOrEmpty(root);
		ArgumentException.ThrowIfNullOrEmpty(railSessionId);
		return JsonSerializer.Serialize(new {
			type = "file-index",
			root,
			files = Array.Empty<string>(),
			pending = true,
			railSessionId,
		});
	}

	/// <summary>
	/// Builds the <c>focus-omnibar</c> message: the page opens Go-to-File preloaded with <paramref name="query"/> —
	/// the reveal for a clicked file link that suffix-matched several workspace files. <paramref name="line"/> is
	/// the link's 1-based line, applied to whichever candidate the user opens.
	/// </summary>
	public static string BuildFocusOmnibar(string query, int line) {
		ArgumentException.ThrowIfNullOrEmpty(query);
		return JsonSerializer.Serialize(new { type = "focus-omnibar", query, line = Math.Max(1, line) });
	}

	/// <summary>Builds the <c>recent-files</c> push (frecency-ranked absolute paths) for the omnibar's Recent section.</summary>
	public static string BuildRecentFiles(IReadOnlyList<string> files) {
		ArgumentNullException.ThrowIfNull(files);
		return JsonSerializer.Serialize(new { type = "recent-files", files });
	}

	/// <summary>Parses a <c>window-control</c> message's <c>action</c>. False for an unknown/missing action.</summary>
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

	/// <summary>Parses a <c>window-resize</c> message's <c>edge</c>. False for an unknown/missing edge.</summary>
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
	/// Parses a <c>menu-action</c> message's <c>action</c> (and optional <c>path</c> for Open Recent). False for
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
