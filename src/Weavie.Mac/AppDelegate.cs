using System.Text.Json;
using CoreGraphics;
using Foundation;
using Weavie.Mac.Hosting;
using WebKit;

namespace Weavie.Mac;

[Register("AppDelegate")]
public sealed class AppDelegate : NSApplicationDelegate
{
    private readonly HostBridge _bridge = new();
    private TerminalController? _terminal;
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
        _terminal = new TerminalController(_bridge);
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

        var autobench = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEAVIE_AUTOBENCH"))
            ? string.Empty
            : "?autobench=1";
        _webView.LoadRequest(new NSUrlRequest(new NSUrl($"app://app/index.html{autobench}")));

        NSApplication.SharedApplication.Activate();

        // Unattended screenshot for the deliverable: fire from the native run loop (not a JS
        // timer, which throttles when the window is occluded). Gated on WEAVIE_SHOT_DIR so the
        // shipped app never writes screenshots.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEAVIE_SHOT_DIR")))
        {
            var delay = double.TryParse(Environment.GetEnvironmentVariable("WEAVIE_SHOT_DELAY"), out var d) ? d : 4.0;
            NSTimer.CreateScheduledTimer(delay, repeats: false, _ => CaptureSnapshot());
        }
    }

    public override bool ApplicationShouldTerminateAfterLastWindowClosed(NSApplication sender) => true;

    public override void WillTerminate(NSNotification notification) => _terminal?.Dispose();

    private void OnWebMessage(string json)
    {
        string type;
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
            type = root.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        }
        catch (JsonException)
        {
            Console.WriteLine($"[weavie] (unparsed) {json}");
            return;
        }

        switch (type)
        {
            case "term-input":
                var input = Convert.FromBase64String(root.GetProperty("dataB64").GetString() ?? string.Empty);
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEAVIE_DEBUG_INPUT")))
                {
                    var printable = string.Concat(input.Select(b => b is >= 0x20 and < 0x7f ? ((char)b).ToString() : $"\\x{b:x2}"));
                    Console.WriteLine($"[weavie] term-input <- xterm ({input.Length}B): {printable}");
                    Console.Out.Flush();
                }
                _terminal?.Write(input);
                break;
            case "term-resize":
                _terminal?.Resize(root.GetProperty("cols").GetInt32(), root.GetProperty("rows").GetInt32());
                break;
            case "term-ready":
                _terminal?.Start(root.GetProperty("cols").GetInt32(), root.GetProperty("rows").GetInt32());
                break;
            default:
                // benchmark-result / latency-live / log / ready — surface for unattended capture.
                Console.WriteLine($"[weavie] {json}");
                Console.Out.Flush();
                break;
        }
    }

    private void CaptureSnapshot()
    {
        var dir = Environment.GetEnvironmentVariable("WEAVIE_SHOT_DIR");
        if (_webView is null || string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        var name = Environment.GetEnvironmentVariable("WEAVIE_SHOT_NAME");
        var path = Path.Combine(dir, string.IsNullOrEmpty(name) ? "step1-latency.png" : name);

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
