using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Weavie.Win.Hosting;

namespace Weavie.NativeBridge.Tests;

internal static class NativeProbe {
	internal static int Run(Probe probe) {
		Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
		using var window = new Form();
		using var view = new WebView2 { Dock = DockStyle.Fill };
		using var bridge = new HostBridge();
		window.Controls.Add(view);
		window.Shown += async (_, _) => {
			try {
				await view.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(userDataFolder: probe.ProfileDirectory));
				var core = view.CoreWebView2;
				bridge.Attach(view);
				bridge.MessageReceived += (_, message) => probe.Receive(message);
				core.NavigationCompleted += (_, e) => {
					if (e.IsSuccess) probe.Loaded();
				};
				core.NavigationStarting += (_, e) => {
					if (e.Cancel) probe.Denied();
				};
				core.FrameNavigationStarting += (_, e) => {
					if (e.Cancel) probe.Denied();
				};
				core.NewWindowRequested += (_, e) => {
					if (e.Handled) probe.Denied();
					else probe.Fail("Native popup was not handled");
				};
				probe.Execute += script => _ = core.ExecuteScriptAsync(script);
				await bridge.Security.LoadAsync(probe.AppUrl, string.Empty, "this.chrome.webview.postMessage.bind(this.chrome.webview)",
					async script => await core.AddScriptToExecuteOnDocumentCreatedAsync(script), core.Navigate);
			} catch (Exception error) {
				probe.Fail(error.ToString());
			}
		};
		Application.Run(window);
		return probe.ExitCode;
	}
}
