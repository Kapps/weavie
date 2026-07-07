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
}
