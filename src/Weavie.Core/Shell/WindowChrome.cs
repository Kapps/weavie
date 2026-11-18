using System.Text.Json.Serialization;

namespace Weavie.Core.Shell;

/// <summary>How a press on empty title-bar space moves the window.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CaptionMode>))]
public enum CaptionMode {
	/// <summary>The bar never moves the window (a browser page, or a window with an OS title bar).</summary>
	[JsonStringEnumMemberName("none")]
	None,

	/// <summary>The page declares the caption with CSS <c>app-region</c> and the web engine owns the gesture (WebView2).</summary>
	[JsonStringEnumMemberName("region")]
	Region,

	/// <summary>The page reports each press and the host turns it into the system's title-bar gesture.</summary>
	[JsonStringEnumMemberName("press")]
	Press,
}

/// <summary>The window buttons the page draws, in order, at each end of its bar.</summary>
public sealed record WindowControls(IReadOnlyList<WindowControl> Start, IReadOnlyList<WindowControl> End) {
	/// <summary>No page-drawn buttons: the OS draws them, or the window has none.</summary>
	public static WindowControls None { get; } = new([], []);
}

/// <summary>Room, in CSS pixels, the bar leaves at each end for buttons the OS draws over it.</summary>
public readonly record struct ChromeInset(int Start, int End);

/// <summary>
/// What a host's window needs from the page's title bar: whether it shows the menus, how the bar moves the window,
/// which buttons the page draws, and the room and height native chrome dictates. Sent at boot, re-sent on change.
/// </summary>
/// <param name="Menus">Whether the bar shows the application menus (macOS shows them in the system menu bar).</param>
/// <param name="Caption">How a press on empty bar space moves the window.</param>
/// <param name="ResizeHandles">Whether the page draws edge grab handles for a frameless window (Windows).</param>
/// <param name="BarHeight">The bar's height in CSS pixels, or null for the stylesheet default.</param>
/// <param name="Controls">The window buttons the page draws.</param>
/// <param name="Inset">Room left for buttons the OS draws over the bar.</param>
public sealed record WindowChrome(
	bool Menus,
	CaptionMode Caption,
	bool ResizeHandles,
	int? BarHeight,
	WindowControls Controls,
	ChromeInset Inset) {
	/// <summary>A browser page: the bar with menus, and no window to move or buttons to draw.</summary>
	public static WindowChrome Browser { get; } = new(true, CaptionMode.None, false, null, WindowControls.None, default);

	/// <summary>A frameless window (Windows): the page draws menus, buttons and edge handles; the web engine owns the caption.</summary>
	public static WindowChrome Frameless { get; } = new(
		true, CaptionMode.Region, true, null,
		new WindowControls([], [WindowControl.Minimize, WindowControl.MaximizeToggle, WindowControl.Close]), default);

	/// <summary>A window whose OS title bar stays: the page draws no bar of its own.</summary>
	public static WindowChrome NativeFrame { get; } = new(false, CaptionMode.None, false, null, WindowControls.None, default);
}

/// <summary>A press on empty title-bar space, as the page saw it: the DOM <c>MouseEvent.button</c> and click count.</summary>
public readonly record struct CaptionPress(int Button, int Count) {
	/// <summary>The DOM number of the primary (left) button.</summary>
	public const int Primary = 0;

	/// <summary>The DOM number of the middle button.</summary>
	public const int Middle = 1;

	/// <summary>The DOM number of the secondary (right) button.</summary>
	public const int Secondary = 2;
}

/// <summary>A window whose chrome never changes and that offers no window operations: headless pages and OS-framed windows.</summary>
public sealed class FixedShellWindow : IShellWindow {
	/// <summary>Creates a window describing itself as <paramref name="chrome"/>.</summary>
	public FixedShellWindow(WindowChrome chrome) {
		ArgumentNullException.ThrowIfNull(chrome);
		Chrome = chrome;
	}

	/// <inheritdoc/>
	public WindowChrome Chrome { get; }

	/// <inheritdoc/>
	public event Action? ChromeChanged {
		add { }
		remove { }
	}
}
