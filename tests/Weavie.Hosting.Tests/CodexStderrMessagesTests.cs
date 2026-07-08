using Weavie.Hosting.Agents.Codex;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class CodexStderrMessagesTests {
	[Fact]
	public void TryFromLine_DropsWarnings() {
		bool emitted = CodexStderrMessages.TryFromLine(
			"""[codex-app-server] {"level":"WARN","fields":{"message":"plugin sync failed"}}""",
			"thread_1",
			out _);

		Assert.False(emitted);
	}

	[Fact]
	public void TryFromLine_MapsStructuredErrors() {
		bool emitted = CodexStderrMessages.TryFromLine(
			"""[codex-app-server] {"level":"ERROR","fields":{"message":"worker quit","error":"No access token was provided"}}""",
			"thread_1",
			out var message);

		Assert.True(emitted);
		Assert.Equal("error", message.Type);
		Assert.Equal("worker quit", message.Summary);
		Assert.Equal("No access token was provided", message.Text);
		Assert.Equal("thread_1", message.ThreadId);
	}
}
