using System.Diagnostics;
using Weavie.Core.Hooks;
using Weavie.Win.Terminal;

namespace Weavie.Win;

internal static class Program {
	/// <summary>
	/// Wall-clock since process entry, used by the <c>diagnostics.startupTiming</c> setting to report
	/// how long each launch phase takes (window → WebView2 ready → navigate). Started before
	/// <see cref="Main"/> runs, so it's the closest "time zero" we can cheaply get to app launch.
	/// </summary>
	internal static readonly Stopwatch StartupClock = Stopwatch.StartNew();

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

		ApplicationConfiguration.Initialize();
		Application.Run(new MainForm());
		return 0;
	}
}
