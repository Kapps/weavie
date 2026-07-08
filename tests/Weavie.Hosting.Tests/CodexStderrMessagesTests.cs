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

	[Fact]
	public void TryFromLine_DropsDuplicateGitHubMcpAuthFailure() {
		bool emitted = CodexStderrMessages.TryFromLine(
			"[codex-app-server] \u001b[2m2026-07-08T02:15:38Z\u001b[0m \u001b[31mERROR\u001b[0m mcp::transport::worker: worker quit with fatal: Transport channel closed, when AuthRequired(AuthRequiredError { www_authenticate_header: \"Bearer error=\\\"invalid_request\\\", error_description=\\\"No access token was provided in this request\\\", resource_metadata=\\\"https://api.githubcopilot.com/.well-known/oauth-protected-resource/mcp/\\\"\" })",
			"thread_1",
			out _);

		Assert.False(emitted);
	}

	[Fact]
	public void TryFromLine_DropsPlainTextCommandStderr() {
		bool emitted = CodexStderrMessages.TryFromLine(
			"[codex-app-server] --check warn if changes introduce conflict markers or whitespace errors",
			"thread_1",
			out _);

		Assert.False(emitted);
	}

	[Fact]
	public void TryFromLine_DropsStructuredCommandExitNoise() {
		bool emitted = CodexStderrMessages.TryFromLine(
			"""[codex-app-server] {"level":"ERROR","fields":{"message":"tool failed","error":"Exit code:1"}}""",
			"thread_1",
			out _);

		Assert.False(emitted);
	}
}
