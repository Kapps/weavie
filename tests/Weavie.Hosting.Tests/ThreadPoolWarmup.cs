using System.Runtime.CompilerServices;

namespace Weavie.Hosting.Tests;

/// <summary>Raises the test process's ThreadPool minimum thread count once at assembly load (see
/// docs/specs/hosting-tests-threadpool-warmup.md). xunit races every test collection's startup work — including
/// production code that intentionally hops off its raising thread via <c>Task.Run</c>, e.g. <c>HostCore</c>'s
/// vanished-workspace detection — through the same pool. At the CLR's default <c>minThreads == ProcessorCount</c>,
/// a CI runner's worth of concurrent collections saturates the pool faster than its throttled thread-injection
/// ramp (about one new thread per 500ms past the minimum) can grow it, so a lone queued <c>Task.Run</c> can sit
/// for seconds behind unrelated tests' work — long enough to trip a 5-second <c>Wait.UntilAsync</c> though the
/// work itself is near-instant.</summary>
internal static class ThreadPoolWarmup {
	[ModuleInitializer]
	internal static void Warm() {
		ThreadPool.GetMinThreads(out int worker, out int io);
		ThreadPool.SetMinThreads(Math.Max(worker, 32), Math.Max(io, 32));
	}
}
