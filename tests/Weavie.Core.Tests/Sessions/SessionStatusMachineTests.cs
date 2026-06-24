using Weavie.Core.Hooks;
using Weavie.Core.Processes;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises how <see cref="SessionStatusMachine"/> maps hook events and supervisor transitions onto a
/// session's <see cref="SessionStatus"/>, plus its change-deduplication.
/// </summary>
public sealed class SessionStatusMachineTests {
	private static HookRequest Hook(HookEventKind kind) => new() {
		Event = kind,
		ToolName = string.Empty,
		ToolInputJson = "{}",
	};

	private static HookRequest Notification(string? message) => new() {
		Event = HookEventKind.Notification,
		ToolName = string.Empty,
		ToolInputJson = "{}",
		Message = message,
	};

	[Fact]
	public void InitialStatus_IsStarting() {
		var machine = new SessionStatusMachine();
		Assert.Equal(SessionStatus.Starting, machine.Status);
	}

	[Fact]
	public void UserPromptSubmit_GoesWorking_AndRaisesChanged() {
		var machine = new SessionStatusMachine();
		SessionStatus? seen = null;
		machine.Changed += s => seen = s;

		machine.Observe(Hook(HookEventKind.UserPromptSubmit));

		Assert.Equal(SessionStatus.Working, machine.Status);
		Assert.Equal(SessionStatus.Working, seen);
	}

	[Fact]
	public void Notification_GoesNeedsInput() {
		var machine = new SessionStatusMachine();
		machine.Observe(Hook(HookEventKind.Notification));
		Assert.Equal(SessionStatus.NeedsInput, machine.Status);
	}

	[Fact]
	public void PermissionNotification_GoesNeedsInput() {
		var machine = new SessionStatusMachine();
		machine.Observe(Notification("Claude needs your permission to use Bash"));
		Assert.Equal(SessionStatus.NeedsInput, machine.Status);
	}

	[Fact]
	public void IdleWaitingNotification_AfterStop_StaysIdle() {
		// The post-turn "waiting for your input" notice arrives after Stop; it must not flip the finished turn
		// back to orange.
		var machine = new SessionStatusMachine();
		machine.Observe(Hook(HookEventKind.Stop));
		machine.Observe(Notification("Claude is waiting for your input"));
		Assert.Equal(SessionStatus.Idle, machine.Status);
	}

	[Fact]
	public void IdleWaitingNotification_DoesNotClearPendingPermission() {
		// A pending permission prompt can also age into a "waiting for your input" notice; that must not clear
		// the genuine NeedsInput.
		var machine = new SessionStatusMachine();
		machine.Observe(Notification("Claude needs your permission to use Bash"));
		machine.Observe(Notification("Claude is waiting for your input"));
		Assert.Equal(SessionStatus.NeedsInput, machine.Status);
	}

	[Fact]
	public void SessionStart_GoesIdle() {
		// A fresh launch leaves Starting for green Idle the moment claude reports it is up.
		var machine = new SessionStatusMachine();
		machine.Observe(Hook(HookEventKind.SessionStart));
		Assert.Equal(SessionStatus.Idle, machine.Status);
	}

	[Fact]
	public void Stop_GoesIdle() {
		var machine = new SessionStatusMachine();
		machine.Observe(Hook(HookEventKind.UserPromptSubmit));
		machine.Observe(Hook(HookEventKind.Stop));
		Assert.Equal(SessionStatus.Idle, machine.Status);
	}

	[Fact]
	public void ToolUse_GoesWorking() {
		var machine = new SessionStatusMachine();
		machine.Observe(Hook(HookEventKind.Stop));

		machine.Observe(Hook(HookEventKind.PreToolUse));
		Assert.Equal(SessionStatus.Working, machine.Status);

		machine.Observe(Hook(HookEventKind.Stop));
		machine.Observe(Hook(HookEventKind.PostToolUse));
		Assert.Equal(SessionStatus.Working, machine.Status);
	}

	[Fact]
	public void OtherEvent_DoesNotChangeStatus() {
		var machine = new SessionStatusMachine();
		machine.Observe(Hook(HookEventKind.Stop));
		machine.Observe(Hook(HookEventKind.Other));
		Assert.Equal(SessionStatus.Idle, machine.Status);
	}

	[Fact]
	public void SupervisorFailed_GoesError() {
		var machine = new SessionStatusMachine();
		machine.ObserveSupervisor(new SupervisorStateChanged(SupervisorState.Failed, 1, 5));
		Assert.Equal(SessionStatus.Error, machine.Status);
	}

	[Fact]
	public void SupervisorCrashBackoff_GoesError() {
		var machine = new SessionStatusMachine();
		machine.ObserveSupervisor(new SupervisorStateChanged(SupervisorState.BackingOff, 139, 0));
		Assert.Equal(SessionStatus.Error, machine.Status);
	}

	[Fact]
	public void SupervisorRestart_GoesStarting() {
		var machine = new SessionStatusMachine();
		machine.Observe(Hook(HookEventKind.Stop));
		machine.ObserveSupervisor(new SupervisorStateChanged(SupervisorState.Running, null, 1));
		Assert.Equal(SessionStatus.Starting, machine.Status);
	}

	[Fact]
	public void SupervisorInitialRunning_RestartCountZero_DoesNotGoStarting() {
		// The first Running (RestartCount 0) is the initial launch, not a crash-restart; only the hook stream
		// owns leaving Starting, so a count-0 Running must not flip a settled status back to Starting.
		var machine = new SessionStatusMachine();
		machine.Observe(Hook(HookEventKind.Stop));

		machine.ObserveSupervisor(new SupervisorStateChanged(SupervisorState.Running, null, 0));

		Assert.Equal(SessionStatus.Idle, machine.Status);
	}

	[Fact]
	public void SupervisorBackoff_WithoutExitCode_DoesNotGoError() {
		// A backoff carrying no exit code isn't a crash report; only a backoff with an exit code becomes Error.
		var machine = new SessionStatusMachine();
		machine.Observe(Hook(HookEventKind.Stop));

		machine.ObserveSupervisor(new SupervisorStateChanged(SupervisorState.BackingOff, null, 1));

		Assert.Equal(SessionStatus.Idle, machine.Status);
	}

	[Fact]
	public void Changed_NotRaised_WhenStatusUnchanged() {
		var machine = new SessionStatusMachine();
		int count = 0;
		machine.Changed += _ => count++;

		machine.Observe(Hook(HookEventKind.PreToolUse));
		machine.Observe(Hook(HookEventKind.PostToolUse));

		Assert.Equal(1, count);
	}
}
