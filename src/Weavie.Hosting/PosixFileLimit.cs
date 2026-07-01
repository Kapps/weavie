using System.Runtime.InteropServices;

namespace Weavie.Hosting;

/// <summary>
/// Raises this process's open-file-descriptor soft limit toward its hard cap. A desktop launch through launchd
/// (a macOS <c>.app</c> from Finder/Dock) inherits a stingy <c>RLIMIT_NOFILE</c> soft limit — 256 — where a
/// terminal launch inherits the shell's far higher <c>ulimit -n</c>. Each Weavie session opens many descriptors
/// (two PTYs, the IDE-MCP + registry sockets and their connections, the hook pipe, language-server stdio, log
/// files), so a second session can exhaust 256 and abort the process mid-switch. Raising the soft limit at
/// startup gives a GUI launch the same headroom a terminal launch already has.
/// </summary>
public static class PosixFileLimit {
	// RLIMIT_NOFILE differs by kernel: 8 on macOS/BSD, 7 on Linux.
	private static int Resource => OperatingSystem.IsMacOS() ? 8 : 7;

	// macOS enforces a per-process ceiling (kern.maxfilesperproc, 10240 by default) regardless of the hard limit;
	// Linux tolerates a far higher soft limit. Clamp the target so setrlimit can't fail by overshooting the cap.
	private static ulong Ceiling => OperatingSystem.IsMacOS() ? 10240UL : 1048576UL;

	[StructLayout(LayoutKind.Sequential)]
	private struct RLimit {
		public ulong Current;
		public ulong Maximum;
	}

	[DllImport("libc", EntryPoint = "getrlimit", SetLastError = true)]
	private static extern int GetRLimit(int resource, out RLimit limit);

	[DllImport("libc", EntryPoint = "setrlimit", SetLastError = true)]
	private static extern int SetRLimit(int resource, ref RLimit limit);

	/// <summary>
	/// Raises the open-file soft limit to the hard limit (clamped to the per-OS ceiling) on macOS/Linux; a no-op
	/// on Windows and when the soft limit is already at or above the target.
	/// </summary>
	/// <param name="log">Sink for a one-line note of the new limit, or why it was left unchanged.</param>
	public static void RaiseToHardLimit(Action<string> log) {
		ArgumentNullException.ThrowIfNull(log);
		if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) {
			return;
		}

		if (GetRLimit(Resource, out var limit) != 0) {
			log($"could not read the open-file limit (errno {Marshal.GetLastPInvokeError()}); leaving it unchanged");
			return;
		}

		ulong target = Math.Min(limit.Maximum, Ceiling);
		if (limit.Current >= target) {
			return;
		}

		ulong previous = limit.Current;
		limit.Current = target;
		if (SetRLimit(Resource, ref limit) != 0) {
			log($"could not raise the open-file limit from {previous} (errno {Marshal.GetLastPInvokeError()}); leaving it unchanged");
			return;
		}

		log($"raised open-file limit {previous} → {target}");
	}
}
