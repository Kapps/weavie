using Weavie.Core.Corrections;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests.Corrections;

/// <summary>
/// The pacing rules for corrections analysis: one run at a time, one completed run per 24 hours, the stamp
/// surviving a restart, and a run that produced nothing leaving the allowance intact.
/// </summary>
public sealed class LearnScheduleTests {
	private const string Path = "/state/learn.json";
	private static readonly DateTimeOffset Start = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public void FirstRun_IsAllowed() {
		var schedule = new LearnSchedule(new InMemoryFileSystem(), Path, new StubClock(Start));

		Assert.True(schedule.Ready);
		Assert.Equal(LearnRefusal.None, schedule.Claim(out string message));
		Assert.Equal(string.Empty, message);
		Assert.Null(schedule.LastResult);
	}

	[Fact]
	public void WhileARunIsInFlight_ReadyIsFalseSoTheNudgeMatchesTheRefusal() {
		var schedule = new LearnSchedule(new InMemoryFileSystem(), Path, new StubClock(Start));
		Assert.Equal(LearnRefusal.None, schedule.Claim(out _));

		Assert.False(schedule.Ready);
		Assert.Equal(LearnRefusal.Running, schedule.Claim(out string message));
		Assert.Contains("already being analyzed", message, StringComparison.Ordinal);
	}

	[Fact]
	public void WithinTwentyFourHoursOfAnAnalyzedRun_IsRefusedWithTheRemainingWait() {
		var clock = new StubClock(Start);
		var schedule = new LearnSchedule(new InMemoryFileSystem(), Path, clock);
		Assert.Equal(LearnRefusal.None, schedule.Claim(out _));
		schedule.Release("<p>rules</p>");

		clock.Now = Start + TimeSpan.FromHours(23);

		Assert.False(schedule.Ready);
		Assert.Equal(LearnRefusal.Cooldown, schedule.Claim(out string message));
		Assert.Contains("once every 24 hours", message, StringComparison.Ordinal);
		Assert.Contains("1 hour", message, StringComparison.Ordinal);
		Assert.Equal("<p>rules</p>", schedule.LastResult); // …and the refusal still has an answer to show
	}

	[Fact]
	public void OnceTheIntervalElapses_AnotherRunIsAllowed() {
		var clock = new StubClock(Start);
		var schedule = new LearnSchedule(new InMemoryFileSystem(), Path, clock);
		Assert.Equal(LearnRefusal.None, schedule.Claim(out _));
		schedule.Release("<p>rules</p>");

		clock.Now = Start + LearnSchedule.Interval;

		Assert.True(schedule.Ready);
		Assert.Equal(LearnRefusal.None, schedule.Claim(out _));
	}

	[Fact]
	public void ARunThatAnalyzedNothing_LeavesTheAllowanceAndTheKeptResultIntact() {
		var clock = new StubClock(Start);
		var schedule = new LearnSchedule(new InMemoryFileSystem(), Path, clock);
		Assert.Equal(LearnRefusal.None, schedule.Claim(out _));
		schedule.Release("<p>kept</p>");
		clock.Now = Start + LearnSchedule.Interval;
		Assert.Equal(LearnRefusal.None, schedule.Claim(out _));

		schedule.Release(null);

		Assert.True(schedule.Ready);
		Assert.Equal("<p>kept</p>", schedule.LastResult);
	}

	[Fact]
	public void TheStampAndTheResultSurviveARestart() {
		var fs = new InMemoryFileSystem();
		var clock = new StubClock(Start);
		var schedule = new LearnSchedule(fs, Path, clock);
		Assert.Equal(LearnRefusal.None, schedule.Claim(out _));
		schedule.Release("<p>rules</p>");
		clock.Now = Start + TimeSpan.FromHours(2);

		var reloaded = new LearnSchedule(fs, Path, clock);

		Assert.False(reloaded.Ready);
		Assert.Equal(LearnRefusal.Cooldown, reloaded.Claim(out _));
		Assert.Equal("<p>rules</p>", reloaded.LastResult);
	}

	[Fact]
	public void AMalformedStamp_ResetsRatherThanWedgingTheFeature() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(Path, "not json at all");

		var schedule = new LearnSchedule(fs, Path, new StubClock(Start));

		Assert.True(schedule.Ready);
		Assert.Equal(LearnRefusal.None, schedule.Claim(out _));
	}

	[Fact]
	public void ClaimAndRelease_RaiseChangedSoTheNudgeReEvaluates() {
		var schedule = new LearnSchedule(new InMemoryFileSystem(), Path, new StubClock(Start));
		int changes = 0;
		schedule.Changed += () => changes++;

		Assert.Equal(LearnRefusal.None, schedule.Claim(out _));
		schedule.Release("<p>rules</p>");

		Assert.Equal(2, changes);
	}

	private sealed class StubClock(DateTimeOffset now) : TimeProvider {
		public DateTimeOffset Now { get; set; } = now;

		public override DateTimeOffset GetUtcNow() => Now;
	}
}
