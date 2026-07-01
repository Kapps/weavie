using System.Runtime.InteropServices;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// <see cref="PosixFileLimit"/> raises the open-file soft limit a launchd (GUI) launch inherits at 256, so a
/// second session can't exhaust it. Verified by lowering the soft limit and asserting it climbs back.
/// </summary>
public sealed class PosixFileLimitTests {
	private const int RlimitNoFile = 7; // Linux value; the suite runs on Linux CI.

	[StructLayout(LayoutKind.Sequential)]
	private struct RLimit {
		public ulong Current;
		public ulong Maximum;
	}

	[DllImport("libc", EntryPoint = "getrlimit", SetLastError = true)]
	private static extern int GetRLimit(int resource, out RLimit limit);

	[DllImport("libc", EntryPoint = "setrlimit", SetLastError = true)]
	private static extern int SetRLimit(int resource, ref RLimit limit);

	[Fact]
	public void RaiseToHardLimit_LiftsALoweredSoftLimit() {
		if (!OperatingSystem.IsLinux()) {
			return; // The P/Invoke constant and the act of lowering are Linux-specific; macOS is exercised in the app.
		}

		Assert.Equal(0, GetRLimit(RlimitNoFile, out var original));
		var lowered = original with { Current = 256 };
		Assert.Equal(0, SetRLimit(RlimitNoFile, ref lowered));
		try {
			PosixFileLimit.RaiseToHardLimit(_ => { });

			Assert.Equal(0, GetRLimit(RlimitNoFile, out var raised));
			Assert.True(raised.Current > 256, $"soft limit should have been raised above 256, was {raised.Current}");
		} finally {
			SetRLimit(RlimitNoFile, ref original);
		}
	}
}
