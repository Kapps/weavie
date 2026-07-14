using System.Reflection;
using Foundation;
using Weavie.Hosting;
using Weavie.Hosting.Web;
using WebKit;

namespace Weavie.Mac;

// The WKWebView bring-up + IWebSurface: the native WebView ops the shared web launcher (Weavie.Hosting.Web) drives,
// plus the Debug dev-server flow and the unattended deliverable screenshot. WKWebView is main-thread-affine, so each
// op marshals onto the main thread via the shared dispatcher.
internal sealed partial class WorkspaceWindow {
#if DEBUG
	// Shared dev-server bring-up; compiled out in Release.
	private DevWebBringUp? _devBringUp;
#endif

	private async Task LoadWebAppAsync() {
		var launcher = new WebAppLauncher(this, _core, string.Empty);
#if DEBUG
		_devBringUp = new DevWebBringUp(
			launcher, this,
			DevWebRoot.Resolve(Assembly.GetExecutingAssembly()),
			line => {
				Console.WriteLine($"[vite] {line}");
				Console.Out.Flush();
			});
		await _devBringUp.RunAsync().ConfigureAwait(false);
#else
		await launcher.LaunchBundleAsync().ConfigureAwait(false);
#endif
	}

	// IWebSurface — WKWebView is main-thread-affine, so each op marshals onto the main thread.
	void IWebSurface.Navigate(string url) =>
		_app.Dispatcher.Post(() => _webView.LoadRequest(new NSUrlRequest(new NSUrl(url))));

	void IWebSurface.RenderHtml(string html) =>
		_app.Dispatcher.Post(() => _webView.LoadHtmlString(new NSString(html), null));

	Task IWebSurface.InjectStartupScriptAsync(string script) {
		var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_app.Dispatcher.Post(() => {
			try {
				_webView.Configuration.UserContentController.AddUserScript(new WKUserScript(
					new NSString(script),
					WKUserScriptInjectionTime.AtDocumentStart,
					isForMainFrameOnly: true));
				tcs.SetResult();
			} catch (Exception ex) {
				tcs.SetException(ex);
			}
		});
		return tcs.Task;
	}

	/// <summary>Unattended deliverable screenshot, gated on WEAVIE_SHOT_DIR so the shipped app never writes one.</summary>
	public void ScheduleSnapshot(ScreenshotRequest shot) {
		ArgumentNullException.ThrowIfNull(shot);
		NSTimer.CreateScheduledTimer(shot.DelaySeconds, repeats: false, _ => CaptureSnapshot(shot));
	}

	private void CaptureSnapshot(ScreenshotRequest shot) {
		Directory.CreateDirectory(shot.DirectoryPath);
		string path = shot.TargetPath;

		_webView.TakeSnapshot(new WKSnapshotConfiguration(), (image, error) => {
			if (image is null) {
				Console.Error.WriteLine($"[weavie] snapshot failed: {error?.LocalizedDescription}");
				return;
			}

			var tiff = image.AsTiff();
			if (tiff is null) {
				return;
			}

			using var rep = new NSBitmapImageRep(tiff);
			var png = rep.RepresentationUsingTypeProperties(NSBitmapImageFileType.Png, new NSDictionary());
			if (png is null) {
				Console.Error.WriteLine("[weavie] snapshot: PNG encoding failed");
				return;
			}

			if (png.Save(path, true, out var saveError)) {
				Console.WriteLine($"[weavie] snapshot saved: {path}");
			} else {
				Console.Error.WriteLine($"[weavie] snapshot save failed: {saveError?.LocalizedDescription}");
			}

			Console.Out.Flush();
		});
	}

#if DEBUG
	/// <summary>Retry button on the dev-server error page: ask the shared bring-up to try again.</summary>
	private async Task RetryDevServerAsync() {
		if (_devBringUp is null) {
			return;
		}

		Console.WriteLine("[weavie] retrying Vite dev server…");
		await _devBringUp.RunAsync().ConfigureAwait(false);
	}

	/// <summary>"Load stale bundle anyway" button: the developer explicitly accepts the possibly-stale bundle.</summary>
	private async Task LoadBundleAsync() {
		if (_devBringUp is null) {
			return;
		}

		Console.WriteLine("[weavie] loading the STALE bundled workspace app (explicit developer choice)");
		await _devBringUp.LoadBundleAsync().ConfigureAwait(false);
	}
#endif
}
