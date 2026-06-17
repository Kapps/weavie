using AppKit;
using Weavie.Core.Hooks;
using Weavie.Mac;

// Hook relay (no UI): claude runs us as its PreToolUse/PostToolUse command hook; forward the event over the
// pipe to the running instance and echo its decision. Must branch before any AppKit init.
if (args.Length > 0 && args[0] == "--hook-relay") {
	return HookRelayClient.Run();
}

NSApplication.Init();

var app = NSApplication.SharedApplication;
app.ActivationPolicy = NSApplicationActivationPolicy.Regular;
app.Delegate = new AppDelegate();
app.Run();
return 0;
