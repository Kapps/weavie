using Weavie.Core.Git;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Repeated edits to an ignored file reuse git's answer until it expires.</summary>
public sealed class IgnoredPathCacheTests {
	private sealed class ManualTime : TimeProvider {
		public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
		public override DateTimeOffset GetUtcNow() => Now;
	}

	[Fact]
	public void IgnoredAnswers_AreReusedUntilTheyExpire_AndOthersAreAlwaysAsked() {
		var time = new ManualTime();
		var queries = new List<string[]>();
		var cache = new IgnoredPathCache(paths => {
			queries.Add([.. paths]);
			return paths.Where(path => path.EndsWith(".log", StringComparison.Ordinal)).ToHashSet();
		}, time);

		Assert.Equal(["/w/a.log"], cache.IgnoredAmong(["/w/a.log", "/w/a.txt"]));
		time.Now += IgnoredPathCache.Lifetime - TimeSpan.FromSeconds(1);
		Assert.Equal(["/w/a.log"], cache.IgnoredAmong(["/w/a.log", "/w/a.txt"]));
		Assert.Equal(["/w/a.log"], cache.IgnoredAmong(["/w/a.log"]));
		time.Now += TimeSpan.FromSeconds(1);
		Assert.Equal(["/w/a.log"], cache.IgnoredAmong(["/w/a.log"]));

		Assert.Equal([["/w/a.log", "/w/a.txt"], ["/w/a.txt"], ["/w/a.log"]], queries);
	}
}
