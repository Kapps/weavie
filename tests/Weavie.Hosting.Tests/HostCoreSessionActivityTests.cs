using System.Text.Json;
using Weavie.Core.Agents;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>The session switcher's activity feed and last-active stamp, published per session.</summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreSessionActivityTests {
	private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(300);

	[Fact]
	public async Task ToolBurst_PublishesOnlyTheLatestLine() {
		await using var host = await TestHost.StartAsync();
		var session = host.WorkspaceSession;
		int replayed = Lines(host).Count;

		session.Events.Observe(new AgentPromptSubmitted(null, "go"));
		foreach (string command in new[] { "ls", "pwd", "pnpm test" }) {
			session.Events.Observe(new AgentActivityObserved(new AgentActivity(AgentActivityKind.Execute, command), AwaitingApproval: false));
		}

		await Wait.UntilAsync(() => {
			host.Time.Advance(Interval);
			return Lines(host).Contains("running pnpm test");
		});
		Assert.Equal(["running pnpm test"], Lines(host).Skip(replayed));
	}

	[Fact]
	public async Task PendingApproval_ReadsAsWantsTo_AndClearsWhenTheTurnEnds() {
		await using var host = await TestHost.StartAsync();
		var session = host.WorkspaceSession;
		var edit = new AgentActivity(AgentActivityKind.Edit, "App.tsx");

		session.Events.Observe(new AgentPromptSubmitted(null, "go"));
		session.Events.Observe(new AgentActivityObserved(edit, AwaitingApproval: true));
		session.Events.Observe(new AgentPermissionResolved(RequiresUserInput: true));
		await AdvanceUntilAsync(host, "wants to edit App.tsx");

		session.Events.Observe(new AgentTurnStopped(WillResume: false));
		await AdvanceUntilAsync(host, null);
	}

	[Fact]
	public async Task StartingWork_StampsLastActive_AndItSurvivesRestart() {
		await using var host = await TestHost.StartAsync();
		long? Stamp() => WorkspaceEntry(host).GetProperty("lastActiveAt") is { ValueKind: JsonValueKind.Number } value
			? value.GetInt64()
			: null;
		Assert.Null(Stamp());

		host.WorkspaceSession.Events.Observe(new AgentPromptSubmitted(null, "go"));

		long expected = ManualTimeProvider.Epoch.ToUnixTimeMilliseconds();
		await Wait.UntilAsync(() => Stamp() == expected);
		await host.RestartAsync();
		Assert.Equal(expected, Stamp());
	}

	private static async Task AdvanceUntilAsync(TestHost host, string? line) =>
		await Wait.UntilAsync(() => {
			host.Time.Advance(Interval);
			var lines = Lines(host);
			return lines.Count > 0 && lines[^1] == line;
		});

	private static List<string?> Lines(TestHost host) =>
		[.. host.Bridge.PostedEvents(host.WorkspaceSession.Address, "status", "activity")
			.Select(message => message.GetProperty("activity").GetString())];

	private static JsonElement WorkspaceEntry(TestHost host) =>
		host.Bridge.LastEvent("sessions", "catalog")!.Value.EnumerateArray()
			.Single(entry => entry.GetProperty("workspaceCheckout").GetBoolean());
}
