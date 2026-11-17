using CoreGraphics;
using Foundation;
using Weavie.Core.Shell;

namespace Weavie.Mac.Hosting;

/// <summary>The system's title-bar double-click action, from the user's Desktop and Dock setting.</summary>
internal enum MacDoubleClickAction {
	/// <summary>Zoom (also "Fill" and the unset default).</summary>
	Zoom,

	/// <summary>Minimize into the Dock.</summary>
	Minimize,

	/// <summary>Do nothing.</summary>
	None,
}

/// <summary>
/// A workspace window's title bar: transparent and untitled, with the page beneath it, and a compact unified toolbar
/// so AppKit centers the traffic lights in a bar of its own height. Static so the Mac test app can exercise it.
/// </summary>
internal static class MacWindowChrome {
	/// <summary>Moves the page under a transparent title bar of compact-toolbar height.</summary>
	public static void Apply(NSWindow window) {
		ArgumentNullException.ThrowIfNull(window);
		window.StyleMask |= NSWindowStyle.FullSizeContentView;
		window.TitlebarAppearsTransparent = true;
		window.TitleVisibility = NSWindowTitleVisibility.Hidden;
		window.Toolbar = new NSToolbar("weavie.titlebar");
		window.ToolbarStyle = NSWindowToolbarStyle.UnifiedCompact;
		window.TitlebarSeparatorStyle = NSTitlebarSeparatorStyle.None;
	}

	/// <summary>The title bar's height and the room the traffic lights take at their end of it, in points.</summary>
	public static (int BarHeight, ChromeInset Inset) Measure(NSWindow window) {
		ArgumentNullException.ThrowIfNull(window);
		window.LayoutIfNeeded();
		double width = window.Frame.Width;
		int barHeight = (int)Math.Round(window.Frame.Height - window.ContentLayoutRect.Height);
		var buttons = CGRect.Null;
		foreach (var kind in new[] { NSWindowButton.CloseButton, NSWindowButton.MiniaturizeButton, NSWindowButton.ZoomButton }) {
			var button = window.StandardWindowButton(kind)
				?? throw new InvalidOperationException($"The window has no {kind}.");
			buttons = buttons.UnionWith(button.ConvertRectToView(button.Bounds, null));
		}

		// The buttons' outer margin mirrored on their inner side, so the bar's content clears them evenly.
		double minX = buttons.GetMinX();
		double maxX = buttons.GetMaxX();
		var inset = (minX + maxX) / 2 < width / 2
			? new ChromeInset((int)Math.Ceiling(maxX + minX), 0)
			: new ChromeInset(0, (int)Math.Ceiling((width - minX) + (width - maxX)));
		return (barHeight, inset);
	}

	/// <summary>
	/// What a title-bar double-click does, from <c>AppleActionOnDoubleClick</c> (unset reads the older
	/// <c>AppleMiniaturizeOnDoubleClick</c>); an unknown value zooms, the system default.
	/// </summary>
	public static MacDoubleClickAction DoubleClickAction() {
		var defaults = NSUserDefaults.StandardUserDefaults;
		return defaults.StringForKey("AppleActionOnDoubleClick") switch {
			"Minimize" => MacDoubleClickAction.Minimize,
			"None" => MacDoubleClickAction.None,
			null when defaults.BoolForKey("AppleMiniaturizeOnDoubleClick") => MacDoubleClickAction.Minimize,
			_ => MacDoubleClickAction.Zoom,
		};
	}
}
