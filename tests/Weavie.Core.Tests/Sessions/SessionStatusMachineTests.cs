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
	public void Changed_NotRaised_WhenStatusUnchanged() {
		var machine = new SessionStatusMachine();
		int count = 0;
		machine.Changed += _ => count++;

		machine.Observe(Hook(HookEventKind.PreToolUse));
		machine.Observe(Hook(HookEventKind.PostToolUse));

		Assert.Equal(1, count);
	}
}
