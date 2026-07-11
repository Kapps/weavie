using Weavie.Core.Agents.Claude;
using Weavie.Core.Changes;
using Weavie.Core.Hooks;
using Weavie.Core.Sessions;

namespace Weavie.Hosting.Tests;

/// <summary>Adapts legacy Claude fixtures to the provider-neutral production consumers.</summary>
internal static class ClaudeAgentTestExtensions {
	public static void Observe(this SessionChangeTracker tracker, HookRequest request) {
		foreach (var value in ClaudeHookEventAdapter.Adapt(request)) {
			tracker.Observe(value);
		}
	}

	public static void ObserveHook(this SessionStatusMachine machine, HookRequest request) {
		foreach (var value in ClaudeHookEventAdapter.Adapt(request)) {
			machine.Observe(value);
		}
	}

	// Routes a hook through the session's production event fan-out (tracker + correction recorder + mode +
	// status), exactly as ClaudeAgentSession.ObserveHook does when the relay pipe delivers it.
	public static void ObserveHook(this HostSession session, HookRequest request) {
		foreach (var value in ClaudeHookEventAdapter.Adapt(request)) {
			session.Events.Observe(value);
		}
	}
}

/// <summary>Minimal <see cref="HookRequest"/> builders for status-machine-driving tests (<c>using static</c> for bare calls).</summary>
internal static class TestHooks {
	public static HookRequest Hook(HookEventKind kind, string? message = null) => new() {
		Event = kind,
		ToolName = "",
		ToolInputJson = "{}",
		Message = message,
	};

	public static HookRequest Stop(bool sessionWillResume) => new() {
		Event = HookEventKind.Stop,
		ToolName = "",
		ToolInputJson = "{}",
		SessionWillResume = sessionWillResume,
	};
}
