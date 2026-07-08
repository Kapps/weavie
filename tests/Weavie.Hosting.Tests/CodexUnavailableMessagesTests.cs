using Weavie.Hosting.Agents.Codex;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class CodexUnavailableMessagesTests {
	[Fact]
	public void TryLaunchFailure_ShowsCodexPathSettingInstructions() {
		bool matched = CodexUnavailableMessages.TryLaunchFailure(
			"[codex-app-server] Warning: launch failed: Access is denied",
			"thread-1",
			"C:\\Program Files\\WindowsApps\\OpenAI.Codex\\codex.exe",
			"C:\\Users\\me\\.weavie\\settings.toml",
			out var message);

		Assert.True(matched);
		Assert.Equal("error", message.Type);
		Assert.Equal("thread-1", message.ThreadId);
		Assert.Contains("Access is denied", message.Text, StringComparison.Ordinal);
		Assert.Contains("Current codex.path: C:\\Program Files\\WindowsApps\\OpenAI.Codex\\codex.exe", message.Text, StringComparison.Ordinal);
		Assert.Contains("Set codex.path", message.Text, StringComparison.Ordinal);
		Assert.Contains("C:\\Users\\me\\.weavie\\settings.toml", message.Text, StringComparison.Ordinal);
	}
}
