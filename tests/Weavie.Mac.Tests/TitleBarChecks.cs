using AppKit;
using CoreGraphics;
using Weavie.Mac.Hosting;
using WebKit;

namespace Weavie.Mac.Tests;

/// <summary>
/// The workspace title bar on a real NSWindow: the compact toolbar centres the traffic lights inside a bar taller than
/// a plain title bar, the measured inset clears them, and a click in the bar away from them reaches the page, even
/// on an inactive window.
/// </summary>
internal static class TitleBarChecks {
	public static void Run() {
		var frame = new CGRect(200, 200, 1000, 700);
		using var webView = new WeavieWebView(frame, new WKWebViewConfiguration());
		var window = new NSWindow(
			frame,
			NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Resizable | NSWindowStyle.Miniaturizable,
			NSBackingStore.Buffered,
			false) {
			ContentView = webView,
		};
		double plainTitleBar = window.Frame.Height - window.ContentLayoutRect.Height;

		MacWindowChrome.Apply(window);
		window.OrderFront(null);
		var (barHeight, inset) = MacWindowChrome.Measure(window);
		var zoom = window.StandardWindowButton(NSWindowButton.ZoomButton)!;
		var zoomFrame = zoom.ConvertRectToView(zoom.Bounds, null);
		double barBottom = window.Frame.Height - barHeight;
		var hit = window.ContentView!.Superview!.HitTest(new CGPoint(window.Frame.Width / 2, window.Frame.Height - (barHeight / 2.0)));
		Console.WriteLine(
			$"titlebar: plain={plainTitleBar} bar={barHeight} inset={inset} zoom={zoomFrame} hit={hit?.GetType().Name ?? "null"}");

		Check(barHeight > plainTitleBar, $"compact toolbar bar ({barHeight}) is taller than a plain title bar ({plainTitleBar})");
		Check(zoomFrame.GetMinY() > barBottom && zoomFrame.GetMaxY() < window.Frame.Height, "zoom button lies inside the bar");
		Check(inset.Start > zoomFrame.GetMaxX() && inset.End == 0, $"inset {inset} clears the traffic lights at the start");
		Check(hit is not null && (hit == webView || hit.IsDescendantOf(webView)), $"a bar click reaches the page, not {hit?.GetType().Name}");
		webView.CaptionHeight = barHeight;
		var barPress = NSEvent.MouseEvent(
			NSEventType.LeftMouseDown, new CGPoint(window.Frame.Width / 2, window.Frame.Height - (barHeight / 2.0)),
			0, 0, window.WindowNumber, null, 0, 1, 1);
		Check(webView.AcceptsFirstMouse(barPress), "an inactive window takes its first click in the bar");

		window.OrderOut(null);
	}

	private static void Check(bool condition, string what) {
		if (!condition) {
			throw new InvalidOperationException($"Title bar: expected {what}.");
		}
	}
}
