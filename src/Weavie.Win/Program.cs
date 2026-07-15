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

		// Reap every spawned child if the host dies by any means; after the smoke branch so the helper invocation
		// doesn't enroll claude's tree. See ChildProcessJob.
		ChildProcessJob.Install();

		ApplicationConfiguration.Initialize();
		using var app = new AppController();
		Application.Run(app);
		return 0;
	}
}
