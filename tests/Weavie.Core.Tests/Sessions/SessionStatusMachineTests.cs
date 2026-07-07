using Weavie.Core.Agents;
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

	private static HookRequest Permission(string toolName) => new() {
		Event = HookEventKind.PermissionRequest,
		ToolName = toolName,
		ToolInputJson = "{}",
	};

	private static HookRequest SessionStart(string source) => new() {
		Event = HookEventKind.SessionStart,
		ToolName = string.Empty,
		ToolInputJson = "{}",
		Source = source,
	};

	private static HookRequest Stop(bool sessionWillResume) => new() {
		Event = HookEventKind.Stop,
		ToolName = string.Empty,
		ToolInputJson = "{}",
		SessionWillResume = sessionWillResume,
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
	public void ApprovedPermissionPrompt_NonEditTool_ClearsNeedsInputOnPostToolUse() {
		// The reported bug: a gated Bash prompt turned the session NeedsInput, but approving it produced no
		// observed event (the old matcher was edit-only), so the status stuck there for the whole tool run.
		// With every tool observed, the approved tool's PostToolUse flips the turn back to Working.
		var machine = new SessionStatusMachine();
		machine.Observe(Notification("Claude needs your permission to use Bash"));
		Assert.Equal(SessionStatus.NeedsInput, machine.Status);

		machine.Observe(new HookRequest { Event = HookEventKind.PostToolUse, ToolName = "Bash", ToolInputJson = "{}" });
		Assert.Equal(SessionStatus.Working, machine.Status);
	}

	[Fact]
	public void PermissionPassedThrough_GoesNeedsInput() {
		// A pass-through PermissionRequest means the dialog is about to appear — NeedsInput without depending
		// on the Notification's wording (covers AskUserQuestion/ExitPlanMode and future dialog kinds).
		var machine = new SessionStatusMachine();
		machine.Observe(Hook(HookEventKind.UserPromptSubmit));

		machine.ObserveDecision(Permission("AskUserQuestion"), HookDecision.PassThrough);

		Assert.Equal(SessionStatus.NeedsInput, machine.Status);
	}

	[Fact]
	public void PermissionAutoAnswered_StaysWorking() {
		// The allow-all gate answers without a dialog; the turn keeps running, so no NeedsInput blip.
		var machine = new SessionStatusMachine();
		machine.Observe(Hook(HookEventKind.UserPromptSubmit));

		machine.ObserveDecision(Permission("Bash"), HookDecision.Allow("Weavie allow-all-tools"));

		Assert.Equal(SessionStatus.Working, machine.Status);
	}

	[Fact]
	public void PermissionDenied_ResumesWorking() {
		// A denied tool sends Claude back to work on the feedback — the prompt is gone, so NeedsInput clears.
		var machine = new SessionStatusMachine();
		machine.ObserveDecision(Permission("Bash"), HookDecision.PassThrough);
		Assert.Equal(SessionStatus.NeedsInput, machine.Status);

		machine.ObserveDecision(Permission("Bash"), HookDecision.Deny("not allowed"));

		Assert.Equal(SessionStatus.Working, machine.Status);
	}

	[Fact]
	public void NonPermissionDecision_DoesNotChangeStatus() {
		// Decisions ride along on every event; only PermissionRequest ones say anything about a dialog.
		var machine = new SessionStatusMachine();
		machine.Observe(Hook(HookEventKind.Stop));

		machine.ObserveDecision(
			new HookRequest { Event = HookEventKind.PostToolUse, ToolName = "Edit", ToolInputJson = "{}" },
			HookDecision.PassThrough);

		Assert.Equal(SessionStatus.Idle, machine.Status);
	}

	[Fact]
	public void IdleWaitingNotification_WhileWorking_GoesIdle() {
		// An interrupted turn (Esc) fires no Stop; the idle "waiting for your input" notice is the only signal
		// the turn ended, so from Working it settles the session to Idle instead of showing Working forever.
		var machine = new SessionStatusMachine();
		machine.Observe(Hook(HookEventKind.UserPromptSubmit));

		machine.Observe(Notification("Claude is waiting for your input"));

		Assert.Equal(SessionStatus.Idle, machine.Status);
	}

	[Fact]
	public void IdleWaitingNotification_WhileStarting_GoesIdle() {
		// Claude came up but the user never typed: the idle notice proves it's up and waiting.
		var machine = new SessionStatusMachine();
		machine.Observe(Notification("Claude is waiting for your input"));
		Assert.Equal(SessionStatus.Idle, machine.Status);
	}

	[Fact]
	public void UserInput_Enter_AnswersThePrompt() {
		// No hook fires when a permission dialog is answered; the Enter keystroke is the approval itself, so it
		// resolves NeedsInput → Working instead of leaving the prompt state up for the whole approved tool run.
		var machine = new SessionStatusMachine();
		machine.Observe(Notification("Claude needs your permission to use Bash"));

		machine.ObserveUserInput("\r"u8.ToArray());

		Assert.Equal(SessionStatus.Working, machine.Status);
	}

	[Fact]
	public void UserInput_BareEscape_AnswersThePrompt() {
		// Esc dismisses the dialog (claude then idles; the idle notice settles the guess if nothing follows).
		var machine = new SessionStatusMachine();
		machine.Observe(Notification("Claude needs your permission to use Bash"));

		machine.ObserveUserInput([0x1b]);

		Assert.Equal(SessionStatus.Working, machine.Status);
	}

	[Fact]
	public void UserInput_EscapeSequences_DoNotAnswerThePrompt() {
		// Arrows move the dialog selection; mouse/focus reports, the terminal's automatic OSC query replies
		// (xterm.js answers claude's theme probe with no user action), and Alt chords aren't answers either.
		var machine = new SessionStatusMachine();
		machine.Observe(Notification("Claude needs your permission to use Bash"));

		machine.ObserveUserInput("\x1b[B"u8.ToArray()); // arrow down (CSI)
		machine.ObserveUserInput("\x1bOA"u8.ToArray()); // arrow up (SS3)
		machine.ObserveUserInput("\x1b]11;rgb:1e1e/1e1e/1e1e\x1b\\"u8.ToArray()); // OSC color-query reply
		machine.ObserveUserInput([0x1b, (byte)'f']); // Alt+f chord
		machine.ObserveUserInput([]);

		Assert.Equal(SessionStatus.NeedsInput, machine.Status);
	}

	[Fact]
	public void UserInput_OutsideNeedsInput_ChangesNothing() {
		// Ordinary typing while claude works (or rests) says nothing about a prompt — there is none.
		var machine = new SessionStatusMachine();
		machine.Observe(Hook(HookEventKind.Stop));

		machine.ObserveUserInput("\r"u8.ToArray());

		Assert.Equal(SessionStatus.Idle, machine.Status);
	}

	[Fact]
	public void SessionStart_Compact_DoesNotDisturbTheTurn() {
		// A mid-turn auto-compact fires SessionStart(source=compact); the turn is still running, so no Idle blip.
		var machine = new SessionStatusMachine();
		machine.Observe(Hook(HookEventKind.UserPromptSubmit));

		machine.Observe(SessionStart("compact"));

		Assert.Equal(SessionStatus.Working, machine.Status);
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
	public void ProviderProcessRestartEvent_GoesStarting() {
		var machine = new SessionStatusMachine();
		machine.Observe(Hook(HookEventKind.Stop));
		machine.Observe(new AgentProcessChanged(new SupervisorStateChanged(SupervisorState.Running, null, 1)));
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
	public void Stop_WithPendingResumption_GoesWaiting() {
		// The overnight-loss case: the turn ended with a pending wakeup / background task (SessionWillResume),
		// so it settles to Waiting — idle-but-not-done — rather than Idle, holding the update drain.
		var machine = new SessionStatusMachine();
		machine.Observe(Hook(HookEventKind.UserPromptSubmit));
		machine.Observe(Stop(sessionWillResume: true));
		Assert.Equal(SessionStatus.Waiting, machine.Status);
	}

	[Fact]
	public void WaitingClears_WhenResumptionResolves() {
		// The wake fires (new turn) and the follow-up ends with nothing pending → genuinely Idle. Each Stop is a
		// fresh snapshot, so Waiting tracks the payload with no leftover state.
		var machine = new SessionStatusMachine();
		machine.Observe(Stop(sessionWillResume: true));
		Assert.Equal(SessionStatus.Waiting, machine.Status);

		machine.Observe(Hook(HookEventKind.UserPromptSubmit));
		machine.Observe(Stop(sessionWillResume: false));
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
