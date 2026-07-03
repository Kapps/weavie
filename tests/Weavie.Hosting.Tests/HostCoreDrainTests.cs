using Weavie.Core.Hooks;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// The update drain gate over a real <see cref="HostCore"/> (docs/specs/runner-auto-update.md): a quiet
/// host commits immediately; a Working / NeedsInput session or a shell foreground job holds the drain
/// (pushed as <c>update-pending</c>) until it settles; the commit freezes terminal input and pushes
/// <c>update-restarting</c>; restart-now skips the gate.
/// </summary>
public sealed class HostCoreDrainTests {
	[Fact]
	public async Task QuietHost_CommitsImmediately_AndFreezesInput() {
		await using var host = await TestHost.StartAsync();
		// A live shell pane, to prove the input freeze at the term-input chokepoint.
		host.Core.ActiveSessionForTest()!.Shell.EnsureStarted();
		var shellTerminal = Assert.Single(host.Platform.NoopLauncher.Created);
		host.Send($$"""{"type":"term-input","slot":"{{host.PrimaryId}}","session":"shell","dataB64":"aGk="}""");
		Assert.Equal(1, shellTerminal.WriteCount);

		bool exited = false;
		host.Core.BeginDrain(() => exited = true);

		Assert.True(exited);
		Assert.NotNull(host.Bridge.LastOfType("update-restarting"));
		// Input submitted after the commit is dropped, not forwarded into a turn the restart would discard.
		host.Send($$"""{"type":"term-input","slot":"{{host.PrimaryId}}","session":"shell","dataB64":"aGk="}""");
		Assert.Equal(1, shellTerminal.WriteCount);
	}

	[Fact]
	public async Task WorkingSession_HoldsDrain_ThenCommitsOnStop() {
		await using var host = await TestHost.StartAsync();
		var session = host.Core.ActiveSessionForTest()!;
		session.Status.Observe(Hook(HookEventKind.UserPromptSubmit));

		bool exited = false;
		host.Core.BeginDrain(() => exited = true);

		Assert.False(exited);
		var pending = host.Bridge.LastOfType("update-pending");
		Assert.NotNull(pending);
		var hold = Assert.Single(pending.Value.GetProperty("holds").EnumerateArray());
		Assert.Equal("working", hold.GetProperty("reason").GetString());

		// The turn settles (Stop hook) → the gate re-evaluates via the session's status subscription.
		session.Status.Observe(Hook(HookEventKind.Stop));
		Assert.True(exited);
		Assert.NotNull(host.Bridge.LastOfType("update-restarting"));
	}

	[Fact]
	public async Task PendingPermissionPrompt_HoldsDrain() {
		await using var host = await TestHost.StartAsync();
		var session = host.Core.ActiveSessionForTest()!;
		session.Status.Observe(Hook(HookEventKind.Notification, message: "Claude needs your permission to use Bash"));

		bool exited = false;
		host.Core.BeginDrain(() => exited = true);

		Assert.False(exited);
		var hold = Assert.Single(host.Bridge.LastOfType("update-pending")!.Value.GetProperty("holds").EnumerateArray());
		Assert.Equal("needs-input", hold.GetProperty("reason").GetString());
	}

	[Fact]
	public async Task ShellForegroundJob_HoldsDrain_UntilItEnds() {
		await using var host = await TestHost.StartAsync();
		var session = host.Core.ActiveSessionForTest()!;
		session.Shell.EnsureStarted();
		var shellTerminal = Assert.Single(host.Platform.NoopLauncher.Created);
		shellTerminal.HasForegroundJob = true;

		bool exited = false;
		host.Core.BeginDrain(() => exited = true);

		Assert.False(exited);
		var hold = Assert.Single(host.Bridge.LastOfType("update-pending")!.Value.GetProperty("holds").EnumerateArray());
		Assert.Equal("shell-job", hold.GetProperty("reason").GetString());

		// The job ends; any status transition re-evaluates the gate (the 2s re-sample tick would too).
		shellTerminal.HasForegroundJob = false;
		session.Status.Observe(Hook(HookEventKind.Stop));
		Assert.True(exited);
	}

	[Fact]
	public async Task ReadyMidDrain_RepushesPendingState() {
		await using var host = await TestHost.StartAsync();
		var session = host.Core.ActiveSessionForTest()!;
		session.Status.Observe(Hook(HookEventKind.UserPromptSubmit));
		host.Core.BeginDrain(() => { });

		// A tab (re)connecting mid-drain must learn the pending state it missed.
		host.Bridge.Clear();
		host.Send("""{"type":"ready"}""");
		Assert.NotNull(host.Bridge.LastOfType("update-pending"));
	}

	[Fact]
	public async Task Ready_PushesHostBuildIdentity() {
		await using var host = await TestHost.StartAsync();
		var info = host.Bridge.LastOfType("host-info");
		Assert.NotNull(info);
		Assert.Equal(HostCore.BuildNumber, info.Value.GetProperty("buildNumber").GetString());
	}

	[Fact]
	public async Task RestartNow_SkipsTheGate() {
		await using var host = await TestHost.StartAsync();
		var session = host.Core.ActiveSessionForTest()!;
		session.Status.Observe(Hook(HookEventKind.UserPromptSubmit));

		bool exited = false;
		host.Core.BeginDrain(() => exited = true);
		Assert.False(exited);

		var result = host.Core.RestartNowForUpdate();
		Assert.True(result.Ok);
		Assert.True(exited);
		Assert.NotNull(host.Bridge.LastOfType("update-restarting"));
	}

	[Fact]
	public async Task RestartNow_WithoutPendingUpdate_Fails() {
		await using var host = await TestHost.StartAsync();
		Assert.False(host.Core.RestartNowForUpdate().Ok);
	}

	[Fact]
	public async Task BeginDrain_IsIdempotent_FirstExitWins() {
		await using var host = await TestHost.StartAsync();
		var session = host.Core.ActiveSessionForTest()!;
		session.Status.Observe(Hook(HookEventKind.UserPromptSubmit));

		int exits = 0;
		host.Core.BeginDrain(() => exits++);
		host.Core.BeginDrain(() => exits += 100);

		session.Status.Observe(Hook(HookEventKind.Stop));
		Assert.Equal(1, exits);
	}

	private static HookRequest Hook(HookEventKind kind, string? message = null) => new() {
		Event = kind,
		ToolName = "",
		ToolInputJson = "{}",
		Message = message,
	};
}
