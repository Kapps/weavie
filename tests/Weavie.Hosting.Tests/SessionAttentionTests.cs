using Weavie.Core.Hooks;
using Xunit;
using static Weavie.Hosting.Tests.TestHooks;

namespace Weavie.Hosting.Tests;

/// <summary>
/// Session attention over a real <see cref="HostCore"/>: a turn
/// settling (Working → Idle) publishes on the owning session bus; a permission
/// prompt pushes <c>needsInput</c>; a self-resuming stop (Waiting) and the trailing idle notice push nothing.
/// This asserts the exact JSON at the bridge seam — the same payload the WSS carries to the web client.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class SessionAttentionTests {
	[Fact]
	public async Task TurnComplete_PushesSessionAttention_WithSlotIdentity() {
		await using var host = await TestHost.StartAsync();
		var session = host.SelectedSession;

		session.Status.ObserveHook(Hook(HookEventKind.UserPromptSubmit));
		session.Status.ObserveHook(Stop(sessionWillResume: false));

		var attention = Assert.Single(
			host.Bridge.PostedEvents(session.Address, "attention", "raised"));
		Assert.Equal("turnComplete", attention.GetProperty("kind").GetString());
		Assert.False(string.IsNullOrEmpty(attention.GetProperty("label").GetString()));

		// The trailing "waiting for your input" notice fires right after Stop; it must not double-ping.
		session.Status.ObserveHook(Hook(HookEventKind.Notification, message: "Claude is waiting for your input"));
		Assert.Single(host.Bridge.PostedEvents(session.Address, "attention", "raised"));
	}

	[Fact]
	public async Task PermissionPrompt_PushesNeedsInput() {
		await using var host = await TestHost.StartAsync();
		var session = host.SelectedSession;

		session.Status.ObserveHook(Hook(HookEventKind.UserPromptSubmit));
		session.Status.ObserveHook(Hook(HookEventKind.Notification, message: "Claude needs your permission to use Bash"));

		var attention = Assert.Single(
			host.Bridge.PostedEvents(session.Address, "attention", "raised"));
		Assert.Equal("needsInput", attention.GetProperty("kind").GetString());
	}

	[Fact]
	public async Task SelfResumingStop_PushesNothing() {
		await using var host = await TestHost.StartAsync();
		var session = host.SelectedSession;

		session.Status.ObserveHook(Hook(HookEventKind.UserPromptSubmit));
		session.Status.ObserveHook(Stop(sessionWillResume: true));

		Assert.Empty(host.Bridge.PostedEvents(session.Address, "attention", "raised"));
	}
}
