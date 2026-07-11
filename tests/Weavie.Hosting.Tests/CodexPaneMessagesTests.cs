using System.Text.Json;
using Weavie.Hosting.Agents.Codex;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class CodexPaneMessagesTests {
	[Fact]
	public void FromRequest_WithoutParams_ProducesPendingInputMessage() {
		using var doc = JsonDocument.Parse("""{"id":4,"method":"item/tool/requestUserInput"}""");
		var message = CodexPaneMessages.FromRequest(new CodexServerRequest("4", 4L, "item/tool/requestUserInput", doc.RootElement.Clone()));

		Assert.Equal("input-requested", message.Type);
		Assert.Equal("4", message.ItemId);
		Assert.Equal("item/tool/requestUserInput", message.ItemType);
		Assert.Equal("pending", message.Status);
	}

	[Fact]
	public void FromRequest_UserInput_SummarizesQuestion() {
		using var doc = JsonDocument.Parse(
			"""{"id":4,"method":"item/tool/requestUserInput","params":{"threadId":"thread_1","turnId":"turn_1","questions":[{"header":"Mode","id":"mode","question":"Which mode?","options":[{"label":"Safe","description":"Use safe mode."}]}]}}""");

		var message = CodexPaneMessages.FromRequest(new CodexServerRequest("input-4", "input-4", "item/tool/requestUserInput", doc.RootElement.Clone()));

		Assert.Equal("input-requested", message.Type);
		Assert.Equal("input-4", message.ItemId);
		Assert.Equal("thread_1", message.ThreadId);
		Assert.Equal("turn_1", message.TurnId);
		Assert.Equal("Which mode?", message.Summary);
	}

	[Fact]
	public void FromRequest_CommandApproval_IncludesCommandText() {
		using var doc = JsonDocument.Parse(
			"""{"id":4,"method":"item/commandExecution/requestApproval","params":{"command":"dotnet test tests/Weavie.Core.Tests/Weavie.Core.Tests.csproj --filter RailStateStoreTests","reason":"Allow the test runner to open its local IPC socket?"}}""");

		var message = CodexPaneMessages.FromRequest(new CodexServerRequest(
			"approval-4", "approval-4", "item/commandExecution/requestApproval", doc.RootElement.Clone()));

		Assert.Equal("approval-requested", message.Type);
		Assert.Equal("Allow the test runner to open its local IPC socket?", message.Summary);
		Assert.Equal("dotnet test tests/Weavie.Core.Tests/Weavie.Core.Tests.csproj --filter RailStateStoreTests", message.Text);
	}

	[Fact]
	public void InputResponse_BuildsAppServerAnswerShape() {
		object response = CodexInputResponses.Build(new Dictionary<string, IReadOnlyList<string>> {
			["mode"] = ["Safe"],
		});

		using var doc = JsonDocument.Parse(JsonSerializer.Serialize(response));

		Assert.Equal("Safe", doc.RootElement.GetProperty("answers").GetProperty("mode").GetProperty("answers")[0].GetString());
	}

	[Fact]
	public void FromNotification_MapsFilePatchUpdated() {
		using var doc = JsonDocument.Parse(
			"""{"method":"item/fileChange/patchUpdated","params":{"threadId":"thread_1","turnId":"turn_1","itemId":"item_1","changes":[{"path":"src/App.cs","diff":"@@","kind":{"type":"update"}}]}}""");

		var message = CodexPaneMessages.FromNotification(
			"item/fileChange/patchUpdated",
			"thread_1",
			doc.RootElement);

		Assert.NotNull(message);
		Assert.Equal("file-patch-updated", message.Type);
		Assert.Equal("thread_1", message.ThreadId);
		Assert.Equal("turn_1", message.TurnId);
		Assert.Equal("item_1", message.ItemId);
		Assert.Equal("src/App.cs", message.Summary);
	}

	[Fact]
	public void FromNotification_DropsPlanDelta() {
		using var doc = JsonDocument.Parse(
			"""{"method":"item/plan/delta","params":{"threadId":"thread_1","turnId":"turn_1","itemId":"item_1","delta":"- inspect"}}""");

		var message = CodexPaneMessages.FromNotification("item/plan/delta", "thread_1", doc.RootElement);

		Assert.Null(message);
	}

	[Fact]
	public void FromNotification_DropsUnknownProtocolNoise() {
		using var doc = JsonDocument.Parse("""{"method":"hook/started","params":{}}""");

		var message = CodexPaneMessages.FromNotification("hook/started", "thread_1", doc.RootElement);

		Assert.Null(message);
	}

	[Fact]
	public void FromNotification_MapsTurnLifecycleAsHiddenPaneState() {
		using var turn = JsonDocument.Parse(
			"""{"method":"turn/completed","params":{"threadId":"thread_1","turn":{"id":"turn_1","status":"completed"}}}""");
		using var interrupted = JsonDocument.Parse(
			"""{"method":"turn/interrupted","params":{"threadId":"thread_1","turn":{"id":"turn_1","status":"interrupted"}}}""");
		using var status = JsonDocument.Parse(
			"""{"method":"thread/status/changed","params":{"threadId":"thread_1","status":""}}""");

		Assert.Equal("turn-completed", CodexPaneMessages.FromNotification("turn/completed", "thread_1", turn.RootElement)?.Type);
		Assert.Equal("turn-interrupted", CodexPaneMessages.FromNotification("turn/interrupted", "thread_1", interrupted.RootElement)?.Type);
		Assert.Null(CodexPaneMessages.FromNotification("thread/status/changed", "thread_1", status.RootElement));
	}

	[Fact]
	public void FromNotification_SummarizesPowerShellCommand() {
		using var doc = JsonDocument.Parse(
			"""{"method":"item/completed","params":{"threadId":"thread_1","turnId":"turn_1","item":{"id":"item_1","type":"commandExecution","status":"completed","command":"\"C:\\WINDOWS\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -Command 'git status --short --branch'"}}}""");

		var message = CodexPaneMessages.FromNotification("item/completed", "thread_1", doc.RootElement);

		Assert.NotNull(message);
		Assert.Equal("git status --short --branch", message.Summary);
	}

	[Fact]
	public void FromNotification_MapsCommandOutputIntoText() {
		using var doc = JsonDocument.Parse(
			"""{"method":"item/completed","params":{"threadId":"thread_1","turnId":"turn_1","item":{"id":"item_1","type":"commandExecution","status":"failed","command":"git diff --check","aggregatedOutput":"src/App.cs: trailing whitespace","exitCode":1}}}""");

		var message = CodexPaneMessages.FromNotification("item/completed", "thread_1", doc.RootElement);

		Assert.NotNull(message);
		Assert.Equal("src/App.cs: trailing whitespace", message.Text);
	}

	[Fact]
	public void FromNotification_DoesNotDuplicateAgentTextIntoSummary() {
		using var doc = JsonDocument.Parse(
			"""{"method":"item/completed","params":{"threadId":"thread_1","turnId":"turn_1","item":{"id":"item_1","type":"agentMessage","status":"completed","text":"Hello"}}}""");

		var message = CodexPaneMessages.FromNotification("item/completed", "thread_1", doc.RootElement);

		Assert.NotNull(message);
		Assert.Null(message.Summary);
		Assert.Equal("Hello", message.Text);
	}

	[Fact]
	public void FromNotification_MapsResolvedRequestIdWithoutJsonQuotes() {
		using var doc = JsonDocument.Parse(
			"""{"method":"serverRequest/resolved","params":{"threadId":"thread_1","requestId":"approval-1"}}""");

		var message = CodexPaneMessages.FromNotification("serverRequest/resolved", "thread_1", doc.RootElement);

		Assert.NotNull(message);
		Assert.Equal("approval-1", message.ItemId);
	}

	[Fact]
	public void FromNotification_MapsFailedMcpStartupToWarning() {
		using var doc = JsonDocument.Parse(
			"""{"method":"mcpServer/startupStatus/updated","params":{"name":"github","status":"failed","error":"No access token was provided"}}""");

		var message = CodexPaneMessages.FromNotification("mcpServer/startupStatus/updated", "thread_1", doc.RootElement);

		Assert.NotNull(message);
		Assert.Equal("warning", message.Type);
		Assert.Equal("MCP server 'github' failed", message.Summary);
		Assert.Equal("No access token was provided", message.Text);
	}

	[Fact]
	public void FromNotification_CompactsGitHubMcpTokenFailure() {
		using var doc = JsonDocument.Parse(
			"""{"method":"mcpServer/startupStatus/updated","params":{"name":"github","status":"failed","error":"GitHub MCP does not support OAuth. bearer_token_env_var = CODEX_GITHUB_PERSONAL_ACCESS_TOKEN"}}""");

		var message = CodexPaneMessages.FromNotification("mcpServer/startupStatus/updated", "thread_1", doc.RootElement);

		Assert.NotNull(message);
		Assert.Equal("warning", message.Type);
		Assert.Equal("GitHub MCP is not authenticated", message.Summary);
		Assert.Equal("Set CODEX_GITHUB_PERSONAL_ACCESS_TOKEN or disable the Codex github MCP server.", message.Text);
	}
}
