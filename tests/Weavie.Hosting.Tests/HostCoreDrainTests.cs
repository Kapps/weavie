using Weavie.Core.Hooks;
using Xunit;
using static Weavie.Hosting.Tests.TestHooks;

namespace Weavie.Hosting.Tests;

/// <summary>
/// The update drain gate over a real <see cref="HostCore"/> (docs/specs/runner-auto-update.md): a quiet
/// host commits immediately; busy sessions, foreground jobs, and recent user input hold it; commit freezes
/// terminal input and pushes <c>update-restarting</c>; restart-now skips the gate.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreDrainTests {
	[Fact]
	public async Task QuietHost_CommitsImmediately_AndFreezesInput() {
		await using var host = await TestHost.StartAsync();
		host.SelectedSession.Shells.Primary!.Controller.EnsureStarted();
		var shellTerminal = Assert.Single(host.Platform.NoopLauncher.Created);

		bool exited = false;
		host.Core.BeginDrain(() => exited = true);

		Assert.True(exited);
		Assert.NotNull(host.Bridge.LastEvent("updates", "restarting"));
		host.SessionEvent(
			host.WorkspaceSession,
			ShellTerminalSet.FeatureName(host.SelectedSession.Shells.Primary!.Id),
			"input",
			new { dataB64 = "aGk=", userInitiated = true });
		Assert.Equal(0, shellTerminal.WriteCount);
	}

	[Fact]
	public async Task RecentTerminalInput_RenewsGraceAndCommitsAtTheBoundary() {
		await using var host = await TestHost.StartAsync();
		host.SelectedSession.Shells.Primary!.Controller.EnsureStarted();
		void Type() => host.SessionEvent(
			host.WorkspaceSession,
				ShellTerminalSet.FeatureName(host.SelectedSession.Shells.Primary!.Id),
			"input",
			new { dataB64 = "aGk=", userInitiated = true });

		Type();
		bool exited = false;
		host.Core.BeginDrain(() => exited = true);

		Assert.False(exited);
		var hold = Assert.Single(host.Bridge.LastEvent("updates", "pending")!.Value
			.GetProperty("holds").EnumerateArray());
		Assert.Equal("recent-input", hold.GetProperty("reason").GetString());

		host.Time.Advance(HostCore.RecentInputGrace - TimeSpan.FromTicks(1));
		host.Core.EvaluateDrainForTest();
		Assert.False(exited);

		Type();
		host.Time.Advance(HostCore.RecentInputGrace - TimeSpan.FromTicks(1));
		host.Core.EvaluateDrainForTest();
		Assert.False(exited);

		host.Time.Advance(TimeSpan.FromTicks(1));
		host.Core.EvaluateDrainForTest();
		Assert.True(exited);
	}

	[Fact]
	public async Task StructuredComposerTyping_HoldsTheAutomaticUpdate() {
		await using var host = await TestHost.StartAsync();
		host.SessionEvent(host.WorkspaceSession, "agent", "typing", new { });

		bool exited = false;
		host.Core.BeginDrain(() => exited = true);

		Assert.False(exited);
		var hold = Assert.Single(host.Bridge.LastEvent("updates", "pending")!.Value
			.GetProperty("holds").EnumerateArray());
		Assert.Equal("recent-input", hold.GetProperty("reason").GetString());

		host.Time.Advance(HostCore.RecentInputGrace - TimeSpan.FromTicks(1));
		host.SessionEvent(host.WorkspaceSession, "agent", "typing", new { });
		host.Time.Advance(HostCore.RecentInputGrace - TimeSpan.FromTicks(1));
		host.Core.EvaluateDrainForTest();
		Assert.False(exited);
	}

	[Fact]
	public async Task TerminalGeneratedReply_DoesNotHoldTheAutomaticUpdate() {
		await using var host = await TestHost.StartAsync();
		host.SelectedSession.Shells.Primary!.Controller.EnsureStarted();
		host.SessionEvent(
			host.WorkspaceSession,
			ShellTerminalSet.FeatureName(host.SelectedSession.Shells.Primary!.Id),
			"input",
			new { dataB64 = "Gw==", userInitiated = false });

		bool exited = false;
		host.Core.BeginDrain(() => exited = true);

		Assert.True(exited);
	}

	[Fact]
	public async Task WorkingSession_HoldsDrain_ThenCommitsOnStop() {
		await using var host = await TestHost.StartAsync();
		var session = host.SelectedSession;
		session.Status.ObserveHook(Hook(HookEventKind.UserPromptSubmit));

		bool exited = false;
		host.Core.BeginDrain(() => exited = true);

		Assert.False(exited);
		var pending = host.Bridge.LastEvent("updates", "pending");
		Assert.NotNull(pending);
		var hold = Assert.Single(pending.Value.GetProperty("holds").EnumerateArray());
		Assert.Equal("working", hold.GetProperty("reason").GetString());

		// The turn settles (Stop hook) → the gate re-evaluates via the session's status subscription.
		session.Status.ObserveHook(Hook(HookEventKind.Stop));
		Assert.True(exited);
		Assert.NotNull(host.Bridge.LastEvent("updates", "restarting"));
	}

	[Fact]
	public async Task PendingPermissionPrompt_HoldsDrain() {
		await using var host = await TestHost.StartAsync();
		var session = host.SelectedSession;
		session.Status.ObserveHook(Hook(HookEventKind.Notification, message: "Claude needs your permission to use Bash"));

		bool exited = false;
		host.Core.BeginDrain(() => exited = true);

		Assert.False(exited);
		var hold = Assert.Single(host.Bridge.LastEvent("updates", "pending")!.Value
			.GetProperty("holds").EnumerateArray());
		Assert.Equal("needs-input", hold.GetProperty("reason").GetString());
	}

	[Fact]
	public async Task ShellForegroundJob_HoldsDrain_UntilItEnds() {
		await using var host = await TestHost.StartAsync();
		var session = host.SelectedSession;
		session.Shells.Primary!.Controller.EnsureStarted();
		var shellTerminal = Assert.Single(host.Platform.NoopLauncher.Created);
		shellTerminal.HasForegroundJob = true;

		bool exited = false;
		host.Core.BeginDrain(() => exited = true);

		Assert.False(exited);
		var hold = Assert.Single(host.Bridge.LastEvent("updates", "pending")!.Value
			.GetProperty("holds").EnumerateArray());
		Assert.Equal("shell-job", hold.GetProperty("reason").GetString());

		// The job ends; any status transition re-evaluates the gate (the 2s re-sample tick would too).
		shellTerminal.HasForegroundJob = false;
		session.Status.ObserveHook(Hook(HookEventKind.Stop));
		await Wait.UntilAsync(() => exited);
	}

	[Fact]
	public async Task WaitingSession_HoldsDrain_UntilTheTaskResolves() {
		// A session that ended its turn with a pending wakeup looks idle but must hold the update.
		await using var host = await TestHost.StartAsync();
		var session = host.SelectedSession;
		session.Status.ObserveHook(Hook(HookEventKind.UserPromptSubmit));
		session.Status.ObserveHook(Stop(sessionWillResume: true));

		bool exited = false;
		host.Core.BeginDrain(() => exited = true);

		Assert.False(exited);
		var hold = Assert.Single(host.Bridge.LastEvent("updates", "pending")!.Value
			.GetProperty("holds").EnumerateArray());
		Assert.Equal("waiting-on-task", hold.GetProperty("reason").GetString());

		// The wake fires and the follow-up ends with nothing pending → genuinely Idle → commit.
		session.Status.ObserveHook(Hook(HookEventKind.UserPromptSubmit));
		session.Status.ObserveHook(Stop(sessionWillResume: false));
		Assert.True(exited);
		Assert.NotNull(host.Bridge.LastEvent("updates", "restarting"));
	}

	[Fact]
	public async Task ReadyMidDrain_RepushesPendingState() {
		await using var host = await TestHost.StartAsync();
		var session = host.SelectedSession;
		session.Status.ObserveHook(Hook(HookEventKind.UserPromptSubmit));
		host.Core.BeginDrain(() => { });

		// A tab (re)connecting mid-drain must learn the pending state it missed.
		host.Bridge.Clear();
		await host.HostRequestAsync<System.Text.Json.JsonElement>("connection", "hello", new { });

		Assert.NotNull(host.Bridge.LastEvent("updates", "pending"));
	}

	[Fact]
	public async Task Ready_PushesHostBuildIdentity() {
		await using var host = await TestHost.StartAsync();
		var hello = await host.HostRequestAsync<System.Text.Json.JsonElement>(
			"connection",
			"hello",
			new { });
		Assert.Equal(HostCore.BuildNumber, hello.GetProperty("buildNumber").GetString());
	}

	[Fact]
	public async Task RestartNow_SkipsRecentInputAndFreezesMoreInput() {
		await using var host = await TestHost.StartAsync();
		host.SelectedSession.Shells.Primary!.Controller.EnsureStarted();
		var shellTerminal = Assert.Single(host.Platform.NoopLauncher.Created);
		host.SessionEvent(
			host.WorkspaceSession,
			ShellTerminalSet.FeatureName(host.SelectedSession.Shells.Primary!.Id),
			"input",
			new { dataB64 = "aGk=", userInitiated = true });

		bool exited = false;
		host.Core.BeginDrain(() => exited = true);
		Assert.True(host.Core.RestartNowForUpdate().Ok);

		Assert.True(exited);
		host.SessionEvent(
			host.WorkspaceSession,
			ShellTerminalSet.FeatureName(host.SelectedSession.Shells.Primary!.Id),
			"input",
			new { dataB64 = "aGk=", userInitiated = true });
		Assert.Equal(1, shellTerminal.WriteCount);
	}

	[Fact]
	public async Task RestartNow_WithoutPendingUpdate_Fails() {
		await using var host = await TestHost.StartAsync();
		Assert.False(host.Core.RestartNowForUpdate().Ok);
	}

	[Fact]
	public async Task BeginDrain_IsIdempotent_FirstExitWins() {
		await using var host = await TestHost.StartAsync();
		var session = host.SelectedSession;
		session.Status.ObserveHook(Hook(HookEventKind.UserPromptSubmit));

		int exits = 0;
		host.Core.BeginDrain(() => exits++);
		host.Core.BeginDrain(() => exits += 100);

		session.Status.ObserveHook(Hook(HookEventKind.Stop));

		Assert.Equal(1, exits);
	}
}
