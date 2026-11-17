using CoreGraphics;
using WebKit;

namespace Weavie.Mac.Hosting;

/// <summary>
/// The workspace page's web view. Records the primary press the page may report as a caption press, so the window can
/// start AppKit's drag from the real event, and takes the first click on an inactive window inside the title-bar strip.
/// </summary>
internal sealed class WeavieWebView : WKWebView {
	private NSEvent? _press;

	public WeavieWebView(CGRect frame, WKWebViewConfiguration configuration) : base(frame, configuration) { }

	/// <summary>The height, in points, of the title-bar strip at the top of the view.</summary>
	public double CaptionHeight { get; set; }

	/// <summary>Whether the primary button is still down since the recorded press.</summary>
	public bool Held { get; private set; }

	/// <summary>Claims the latest primary press for a caption report; null when there is none or it was claimed.</summary>
	public NSEvent? TakePress() {
		var press = _press;
		_press = null;
		return press;
	}

	public override void MouseDown(NSEvent theEvent) {
		_press = theEvent;
		Held = true;
		base.MouseDown(theEvent);
	}

	public override void MouseUp(NSEvent theEvent) {
		Held = false;
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
