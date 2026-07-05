using System.Text.Json;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// End-to-end wiring for built-in workspace auto-config: opening a supported repo detects the language, writes
/// the setup command + test profile, drops the setup card, and toasts — deterministically, no model. An
/// unsupported repo leaves the card up (the Claude fallback). See docs/concepts/workspace-autoconfig.md.
/// </summary>
[Collection("host-integration")]
public sealed class HostCoreAutoConfigTests {
	[Fact]
	public async Task SupportedRepo_AutoConfigures_DropsCard_AndNotifies() {
		await using var host = await TestHost.StartAsync(repo =>
			File.WriteAllText(Path.Combine(repo, "go.mod"), "module weavie/test\n"));

		// Settled state: the toast is posted and the card is gone (both settings written). Card-gone with a
		// manifest present proves BOTH worktree.setupCommand and test.profile were written (the card's gate).
		await WaitUntilAsync(() => AutoConfigToast(host) is not null && !CardShowing(host), "auto-config to settle");

		var toast = AutoConfigToast(host)!.Value;
		Assert.Equal("info", toast.GetProperty("level").GetString());
		string message = toast.GetProperty("message").GetString()!;
		Assert.Contains("Configured", message, StringComparison.Ordinal);
		Assert.Contains("Go", message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Toast_IsHeldUntilAPageConnects_ThenDelivered() {
		// The probe writes during startup, before any page exists; the toast must be held (PostToWeb drops with no
		// client), not fired-and-lost. Start WITHOUT ready, let the write land, and assert the toast isn't out yet.
		await using var host = await TestHost.StartAsync(
			repo => File.WriteAllText(Path.Combine(repo, "go.mod"), "module weavie/test\n"), sendReady: false);

		await WaitUntilAsync(() => ProfileWritten(host), "auto-config to write the profile");
		Assert.Null(AutoConfigToast(host)); // held, not dropped

		host.Send("""{"type":"ready"}""");

		await WaitUntilAsync(() => AutoConfigToast(host) is not null, "the toast to arrive after ready");
	}

	// A test-profile push carrying real content means the probe wrote test.profile (fired via SettingChanged).
	private static bool ProfileWritten(TestHost host) =>
		host.Bridge.PostedOfType("test-profile").Any(m => !string.IsNullOrEmpty(m.GetProperty("profile").GetString()));

	[Fact]
	public async Task UnsupportedRepo_LeavesCardUp_ForTheClaudeFallback() {
		await using var host = await TestHost.StartAsync(repo =>
			File.WriteAllText(Path.Combine(repo, "Makefile"), "all:\n\techo hi\n"));

		// A manifest is present but no preset matched → the card appears (unsupported → Claude), nothing written.
		await WaitUntilAsync(() => CardShowing(host), "the setup card");
		Assert.Null(AutoConfigToast(host));
	}

	private static JsonElement? AutoConfigToast(TestHost host) {
		foreach (var message in host.Bridge.PostedOfType("notify")) {
			if (message.TryGetProperty("key", out var key) && key.GetString() == "workspace-autoconfig") {
				return message;
			}
		}

		return null;
	}

	private static bool CardShowing(TestHost host) {
		var suggestions = host.Bridge.LastOfType("suggestions");
		return suggestions.HasValue && suggestions.Value.GetProperty("items").EnumerateArray()
			.Any(item => item.GetProperty("id").GetString() == "workspace.setup");
	}

	private static async Task WaitUntilAsync(Func<bool> condition, string what) {
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
		while (DateTime.UtcNow < deadline) {
			if (condition()) {
				return;
			}

			await Task.Delay(25);
		}

		throw new TimeoutException($"timed out waiting for {what}");
	}
}
