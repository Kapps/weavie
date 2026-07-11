using Weavie.Core.Hooks;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// The session-attention push over a real <see cref="HostCore"/> (docs/specs/session-attention.md): a turn
/// settling (Working → Idle) pushes <c>session-attention</c> carrying the slot's rail identity; a permission
/// prompt pushes <c>needsInput</c>; a self-resuming stop (Waiting) and the trailing idle notice push nothing.
/// This asserts the exact JSON at the bridge seam — the same payload the WSS carries to the web client.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class SessionAttentionTests {
	[Fact]
	public async Task TurnComplete_PushesSessionAttention_WithSlotIdentity() {
		await using var host = await TestHost.StartAsync();
		var session = host.Core.ActiveSessionForTest()!;

		session.Status.ObserveHook(Hook(HookEventKind.UserPromptSubmit));
		session.Status.ObserveHook(Stop(sessionWillResume: false));

		var attention = Assert.Single(host.Bridge.PostedOfType("session-attention"));
		Assert.Equal(host.PrimaryId, attention.GetProperty("slot").GetString());
		Assert.Equal("turnComplete", attention.GetProperty("kind").GetString());
		Assert.Equal("claude", attention.GetProperty("providerId").GetString());
		Assert.False(string.IsNullOrEmpty(attention.GetProperty("label").GetString()));

		// The trailing "waiting for your input" notice fires right after Stop; it must not double-ping.
		session.Status.ObserveHook(Hook(HookEventKind.Notification, message: "Claude is waiting for your input"));
		Assert.Single(host.Bridge.PostedOfType("session-attention"));
	}

	[Fact]
	public async Task PermissionPrompt_PushesNeedsInput() {
		await using var host = await TestHost.StartAsync();
		var session = host.Core.ActiveSessionForTest()!;

		session.Status.ObserveHook(Hook(HookEventKind.UserPromptSubmit));
		session.Status.ObserveHook(Hook(HookEventKind.Notification, message: "Claude needs your permission to use Bash"));

		var attention = Assert.Single(host.Bridge.PostedOfType("session-attention"));
		Assert.Equal("needsInput", attention.GetProperty("kind").GetString());
		Assert.Equal(host.PrimaryId, attention.GetProperty("slot").GetString());
	}

	[Fact]
	public async Task SelfResumingStop_PushesNothing() {
		await using var host = await TestHost.StartAsync();
		var session = host.Core.ActiveSessionForTest()!;

		session.Status.ObserveHook(Hook(HookEventKind.UserPromptSubmit));
		session.Status.ObserveHook(Stop(sessionWillResume: true));

		Assert.Empty(host.Bridge.PostedOfType("session-attention"));
	}

	private static HookRequest Hook(HookEventKind kind, string? message = null) => new() {
		Event = kind,
		ToolName = "",
		ToolInputJson = "{}",
		Message = message,
	};

	private static HookRequest Stop(bool sessionWillResume) => new() {
		Event = HookEventKind.Stop,
		ToolName = "",
		ToolInputJson = "{}",
		SessionWillResume = sessionWillResume,
	};
}
