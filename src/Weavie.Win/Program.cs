using Weavie.Core.Hooks;
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

		// Hook relay (no UI): claude runs us as its PreToolUse/PostToolUse command hook; forward the event
		// to the running instance over the pipe and echo its decision. Must branch before any UI init.
		if (args.Length > 0 && string.Equals(args[0], "--hook-relay", StringComparison.Ordinal)) {
			return HookRelayClient.Run();
		}

		// Reap every child we spawn (Vite, ConPTY shells, language servers, claude) if this host dies by any
		// means — clean exit, a debugger Stop, or a crash — via a kill-on-close Job Object the children inherit.
		// The OS backstop behind ProcessSupervisor's graceful teardown; nothing can orphan. Installed after the
		// transient relay/smoke branches above so those short-lived helper invocations don't enroll claude's tree.
		ChildProcessJob.Install();

		ApplicationConfiguration.Initialize();
		Application.Run(new AppController());
		return 0;
	}
}
