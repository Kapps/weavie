using AppKit;
using CoreGraphics;
using Foundation;
using Weavie.Mac.Hosting;
using WebKit;

namespace Weavie.NativeBridge.Tests;

internal static class NativeProbe {
	internal static int Run(Probe probe) {
		NSApplication.Init();
		using var configuration = new WKWebViewConfiguration();
		configuration.WebsiteDataStore = WKWebsiteDataStore.NonPersistentDataStore;
		configuration.Preferences.JavaScriptCanOpenWindowsAutomatically = true;
		using var bridge = new HostBridge();
		configuration.UserContentController.AddScriptMessageHandler(bridge, "weavie");
		using var view = new WKWebView(new CGRect(0, 0, 640, 480), configuration);
		using var window = new NSWindow(new CGRect(0, 0, 640, 480), NSWindowStyle.Titled, NSBackingStore.Buffered, false) { ContentView = view };
		window.MakeKeyAndOrderFront(null);
		bridge.Attach(view);
		using var observer = new PolicyObserver((WKNavigationDelegate)view.NavigationDelegate!, probe);
		view.NavigationDelegate = observer;
		bridge.MessageReceived += (_, message) => probe.Receive(message);
		probe.Execute += script => NSApplication.SharedApplication.BeginInvokeOnMainThread(() => view.EvaluateJavaScript(script, (_, error) => {
			if (error is not null) probe.Fail(error.LocalizedDescription);
		}));
		probe.Navigate += url => NSApplication.SharedApplication.BeginInvokeOnMainThread(() => view.LoadRequest(new NSUrlRequest(new NSUrl(url))));
		bridge.Security.LoadAsync(probe.AppUrl, string.Empty, "this.webkit.messageHandlers.weavie.postMessage.bind(this.webkit.messageHandlers.weavie)",
			script => {
				configuration.UserContentController.AddUserScript(new WKUserScript(new NSString(script), WKUserScriptInjectionTime.AtDocumentStart, true));
				return Task.CompletedTask;
			}, url => view.LoadRequest(new NSUrlRequest(new NSUrl(url)))).GetAwaiter().GetResult();
		NSApplication.SharedApplication.Run();
		return probe.ExitCode;
	}

	private sealed class PolicyObserver(WKNavigationDelegate policy, Probe probe) : WKNavigationDelegate {
		public override void DecidePolicy(WKWebView webView, WKNavigationAction action, Action<WKNavigationActionPolicy> decide) =>
			policy.DecidePolicy(webView, action, result => {
				decide(result);
				if (result == WKNavigationActionPolicy.Cancel) probe.Denied();
			});

		public override void DecidePolicy(WKWebView webView, WKNavigationResponse response, Action<WKNavigationResponsePolicy> decide) =>
			policy.DecidePolicy(webView, response, result => {
				decide(result);
				if (result == WKNavigationResponsePolicy.Cancel) probe.Denied();
			});

		public override void DidFinishNavigation(WKWebView webView, WKNavigation navigation) => probe.Loaded();
	}
}
