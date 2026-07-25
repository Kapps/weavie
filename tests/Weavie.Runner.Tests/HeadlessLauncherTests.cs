using Xunit;

namespace Weavie.Runner.Tests;

public sealed class HeadlessLauncherTests {
	[Theory]
	// The exact .NET exception text observed in CI (kapps/weavie#461, run 30145704093/job 89647138795): two
	// concurrent runners' AllocatePort() picks collided, and the worker crash-looped trying to rebind the
	// same doomed port forever until the test's own deadline gave up.
	[InlineData(
		"[backend] Unhandled exception. System.AggregateException: One or more host shutdown operations failed. (Failed to bind to address http://127.0.0.1:35005: address already in use.)")]
	[InlineData(
		"[backend]  ---> System.Net.Sockets.SocketException (98): Address already in use")]
	[InlineData("ADDRESS ALREADY IN USE")]
	public void IsPortConflictLine_MatchesTheBindFailureSignature(string line) =>
		Assert.True(HeadlessLauncher.IsPortConflictLine(line));

	[Theory]
	[InlineData("[backend] info: starting")]
	[InlineData("[backend] Unhandled exception. System.NullReferenceException: Object reference not set")]
	[InlineData("")]
	public void IsPortConflictLine_IgnoresUnrelatedCrashes(string line) =>
		Assert.False(HeadlessLauncher.IsPortConflictLine(line));
}
