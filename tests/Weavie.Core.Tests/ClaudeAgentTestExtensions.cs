global using Weavie.Core.Tests.Support;

using Weavie.Core.Agents;
using Weavie.Core.Agents.Claude;
using Weavie.Core.Changes;
using Weavie.Core.Hooks;
using Weavie.Core.Sessions;

namespace Weavie.Core.Tests.Support;

/// <summary>Adapts legacy Claude fixtures to the provider-neutral production consumers.</summary>
internal static class ClaudeAgentTestExtensions {
	public static void Observe(this SessionChangeTracker tracker, HookRequest request) {
		foreach (var value in ClaudeHookEventAdapter.Adapt(request)) {
			tracker.Observe(value);
		}
	}

	public static string? EditLocationFor(this SessionChangeTracker tracker, HookRequest request) =>
		ClaudeHookEventAdapter.Adapt(request).Select(tracker.EditLocationFor).FirstOrDefault(value => value is not null);

	public static void Observe(this SessionStatusMachine machine, HookRequest request) {
		foreach (var value in ClaudeHookEventAdapter.Adapt(request)) {
			machine.Observe(value);
		}
	}

	public static void ObserveDecision(
		this SessionStatusMachine machine,
		HookRequest request,
		HookDecision decision) {
		if (request.Event == HookEventKind.PermissionRequest) {
			machine.Observe(new AgentPermissionResolved(decision.Kind == HookDecisionKind.PassThrough));
		}
	}

	public static void Observe(this ObservedPermissionMode mode, HookRequest request) {
		foreach (var value in ClaudeHookEventAdapter.Adapt(request)) {
			mode.Observe(value);
		}
	}
}
