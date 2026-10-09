using AppKit;
using Weavie.Hosting;
using Weavie.Hosting.Desktop;
using Weavie.Mac;

NSApplication.Init();

var app = NSApplication.SharedApplication;
app.ActivationPolicy = NSApplicationActivationPolicy.Regular;
app.Delegate = new AppDelegate();
using var lifetime = new PosixApplicationLifetime(
	new DelegateUiDispatcher(app.BeginInvokeOnMainThread), () => app.Terminate(app));
app.Run();
return 0;
