using Weavie.Win.Hosting;
using Weavie.Win.Terminal;

namespace Weavie.Win;

internal static class Program {
	[STAThread]
	private static int Main(string[] args) {
		// Headless ConPTY self-check (no UI); see PtySmoke.
		if (args.Length > 0 && string.Equals(args[0], "--pty-smoke", StringComparison.Ordinal)) {
			return PtySmoke.Run();
		}

		// Reap every child we spawn (Vite, ConPTY shells, language servers, claude) if this host dies by any
		// means — clean exit, a debugger Stop, or a crash — via a kill-on-close Job Object the children inherit.
		// The OS backstop behind ProcessSupervisor's graceful teardown; nothing can orphan. Installed after the
		// transient smoke branch above so that short-lived helper invocation doesn't enroll claude's tree.
		ChildProcessJob.Install();

		ApplicationConfiguration.Initialize();
		Application.Run(new AppController());
		return 0;
	}
}
