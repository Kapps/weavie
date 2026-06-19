using Weavie.Core.Hooks;
using Weavie.Linux;
using Weavie.Linux.Native;

// Hook relay (no UI): claude runs us as its PreToolUse/PostToolUse command hook; forward the event over
// the pipe to the running instance and echo its decision. Must branch before any GTK init.
if (args.Length > 0 && args[0] == "--hook-relay") {
	return HookRelayClient.Run();
}

Gtk.gtk_init(IntPtr.Zero, IntPtr.Zero);

var host = new WorkspaceHost();
host.Start();
Gtk.gtk_main();
host.Shutdown();
return 0;
