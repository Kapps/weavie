using CoreGraphics;
using Weavie.Core.Shell;
using WebKit;

namespace Weavie.Mac.Hosting;

/// <summary>
/// The workspace page's web view. Records primary presses for the window's <see cref="CaptionGesture"/>, keeping the
/// event so AppKit's drag can start from it, and takes the first click on an inactive window inside the title bar.
/// </summary>
internal sealed class WeavieWebView : WKWebView {
	public WeavieWebView(CGRect frame, WKWebViewConfiguration configuration) : base(frame, configuration) { }

	/// <summary>The presses the page's caption reports are paired with.</summary>
	public CaptionGesture Caption { get; } = new();

	/// <summary>The latest primary mouse-down, which a drag starts from.</summary>
	public NSEvent? LastPress { get; private set; }

	/// <summary>The height, in points, of the title-bar strip at the top of the view.</summary>
	public double CaptionHeight { get; set; }

	public override void MouseDown(NSEvent theEvent) {
		LastPress = theEvent;
		Caption.NativePress(CaptionPress.Primary);
		base.MouseDown(theEvent);
	}

	public override void MouseUp(NSEvent theEvent) {
		Caption.NativeRelease();
		base.MouseUp(theEvent);
	}

	public override bool AcceptsFirstMouse(NSEvent? theEvent) {
		if (theEvent is null) {
			return base.AcceptsFirstMouse(theEvent);
		}

		var point = ConvertPointFromView(theEvent.LocationInWindow, null);
		double fromTop = IsFlipped ? point.Y : Bounds.Height - point.Y;
		return fromTop <= CaptionHeight || base.AcceptsFirstMouse(theEvent);
	}
}
