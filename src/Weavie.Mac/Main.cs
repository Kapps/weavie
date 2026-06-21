using AppKit;
using Weavie.Mac;

NSApplication.Init();

var app = NSApplication.SharedApplication;
app.ActivationPolicy = NSApplicationActivationPolicy.Regular;
app.Delegate = new AppDelegate();
app.Run();
return 0;
