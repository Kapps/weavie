using CoreGraphics;
using Foundation;
using Weavie.Mac.Hosting;
using WebKit;

namespace Weavie.Mac;

[Register("AppDelegate")]
public sealed class AppDelegate : NSApplicationDelegate
{
    private readonly HostBridge _bridge = new();
    private NSWindow? _window;
    private WKWebView? _webView;

    public override void DidFinishLaunching(NSNotification notification)
    {
        var resourcePath = NSBundle.MainBundle.ResourcePath
            ?? throw new InvalidOperationException("No bundle resource path.");
        var wwwroot = Path.Combine(resourcePath, "wwwroot");

        var config = new WKWebViewConfiguration();
        config.SetUrlSchemeHandler(new AppSchemeHandler(wwwroot), "app");
        config.UserContentController.AddScriptMessageHandler(_bridge, "weavie");
        // Allow the Web Inspector for local debugging of the prototype.
        config.Preferences.SetValueForKey(NSNumber.FromBoolean(true), new NSString("developerExtrasEnabled"));

        var frame = new CGRect(0, 0, 1280, 840);
        _webView = new WKWebView(frame, config);
        _bridge.Attach(_webView);
        _bridge.MessageReceived += OnWebMessage;

        _window = new NSWindow(
            frame,
            NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Resizable | NSWindowStyle.Miniaturizable,
            NSBackingStore.Buffered,
            false)
        {
            Title = "weavie",
            ContentView = _webView,
        };
        _window.Center();
        _window.MakeKeyAndOrderFront(null);

        _webView.LoadRequest(new NSUrlRequest(new NSUrl("app://app/index.html")));

        NSApplication.SharedApplication.Activate();

        // Unattended screenshot for the deliverable: fire from the native run loop (not a JS
        // timer, which throttles when the window is occluded). Gated on WEAVIE_SHOT_DIR so the
        // shipped app never writes screenshots.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEAVIE_SHOT_DIR")))
        {
            NSTimer.CreateScheduledTimer(4.0, repeats: false, _ => CaptureSnapshot());
        }
    }

    public override bool ApplicationShouldTerminateAfterLastWindowClosed(NSApplication sender) => true;

    private static void OnWebMessage(string json)
    {
        Console.WriteLine($"[weavie] {json}");
        Console.Out.Flush();
    }

    private void CaptureSnapshot()
    {
        var dir = Environment.GetEnvironmentVariable("WEAVIE_SHOT_DIR");
        if (_webView is null || string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "step1-latency.png");

        _webView.TakeSnapshot(new WKSnapshotConfiguration(), (image, error) =>
        {
            if (image is null)
            {
                Console.Error.WriteLine($"[weavie] snapshot failed: {error?.LocalizedDescription}");
                return;
            }

            var tiff = image.AsTiff();
            if (tiff is null)
            {
                return;
            }

            using var rep = new NSBitmapImageRep(tiff);
            var png = rep.RepresentationUsingTypeProperties(NSBitmapImageFileType.Png, new NSDictionary());
            if (png is null)
            {
                Console.Error.WriteLine("[weavie] snapshot: PNG encoding failed");
                return;
            }

            if (png.Save(path, true, out var saveError))
            {
                Console.WriteLine($"[weavie] snapshot saved: {path}");
            }
            else
            {
                Console.Error.WriteLine($"[weavie] snapshot save failed: {saveError?.LocalizedDescription}");
            }

            Console.Out.Flush();
        });
    }
}
