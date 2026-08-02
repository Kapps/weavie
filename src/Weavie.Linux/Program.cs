using Weavie.Linux;
using Weavie.Linux.Hosting;
using Weavie.Linux.Native;

LinuxGraphicsCompatibility.Apply();
Gtk.gtk_init(IntPtr.Zero, IntPtr.Zero);

var host = new WorkspaceHost();
host.Start();
Gtk.gtk_main();
host.Shutdown();
return 0;
