using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Hosting.Agents;
using Weavie.Hosting.Agents.Codex;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class CodexPaneMessagesTests {
	[Fact]
	public void AgentPaneProtocol_SerializesNormalizedQuestionsForTheWeb() {
		string json = AgentPaneProtocol.Message("slot-1", "/repo", new AgentPaneMessage {
			Type = "input-requested",
			ProviderId = "codex",
			Questions = [new AgentInputQuestion {
				Id = "mode",
				Header = "Mode",
				Question = "Which mode?",
				IsSecret = false,
				Options = [new AgentInputOption { Label = "Safe", Description = "Use safe mode." }],
			}],
		});

		using var doc = JsonDocument.Parse(json);
		var question = doc.RootElement.GetProperty("message").GetProperty("questions")[0];
		Assert.Equal("mode", question.GetProperty("id").GetString());
		Assert.Equal("Safe", question.GetProperty("options")[0].GetProperty("label").GetString());
		Assert.False(question.TryGetProperty("Id", out _));
	}

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
		var question = Assert.Single(message.Questions!);
		Assert.Equal("mode", question.Id);
		Assert.Equal("Mode", question.Header);
		Assert.False(question.IsSecret);
		Assert.Equal("Safe", Assert.Single(question.Options).Label);
	}

	[Fact]
	public void FromRequest_CommandApproval_ShowsTheCommand() {
		using var doc = JsonDocument.Parse(
			"""{"id":"approval-1","method":"item/commandExecution/requestApproval","params":{"threadId":"thread_1","turnId":"turn_1","itemId":"item_1","command":"dotnet test tests/Weavie.Hosting.Tests","cwd":"/repo","reason":"Verify the change."}}""");

		var message = CodexPaneMessages.FromRequest(new CodexServerRequest("approval-1", "approval-1", "item/commandExecution/requestApproval", doc.RootElement.Clone()));

		Assert.Equal("approval-requested", message.Type);
		Assert.Equal("Verify the change.", message.Summary);
		Assert.Equal("dotnet test tests/Weavie.Hosting.Tests", message.Text);
	}

	[Fact]
	public void FromRequest_FileChangeApproval_HasNoParamsSubstance() {
		// Real fileChange approval params carry only ids/reason/grantRoot — the changed paths are joined by
		// the session from the item's own notifications, never read from the request.
		using var doc = JsonDocument.Parse(
			"""{"id":"approval-2","method":"item/fileChange/requestApproval","params":{"threadId":"thread_1","turnId":"turn_1","itemId":"item_2","startedAtMs":1,"reason":"apply the patch"}}""");

		var message = CodexPaneMessages.FromRequest(new CodexServerRequest("approval-2", "approval-2", "item/fileChange/requestApproval", doc.RootElement.Clone()));

		Assert.Equal("approval-requested", message.Type);
		Assert.Equal("apply the patch", message.Summary);
		Assert.Null(message.Text);
	}

	[Fact]
	public void FromRequest_McpElicitation_SurfacesPromptMessage() {
		using var doc = JsonDocument.Parse(
			"""{"id":"approval-4","method":"mcpServer/elicitation/request","params":{"serverName":"functions","threadId":"thread_1","turnId":"turn_1","mode":"openai/form","message":"Allow Git to update this worktree?","requestedSchema":{}}}""");

		var message = CodexPaneMessages.FromRequest(new CodexServerRequest(
			"approval-4", "approval-4", "mcpServer/elicitation/request", doc.RootElement.Clone()));

		Assert.Equal("approval-requested", message.Type);
		Assert.Equal("Allow Git to update this worktree?", message.Summary);
		Assert.Equal("pending", message.Status);
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
	public void FromNotification_MapsPlanDelta() {
		using var doc = JsonDocument.Parse(
			"""{"method":"item/plan/delta","params":{"threadId":"thread_1","turnId":"turn_1","itemId":"item_1","delta":"- inspect"}}""");

		var message = CodexPaneMessages.FromNotification("item/plan/delta", "thread_1", doc.RootElement);

		Assert.NotNull(message);
		Assert.Equal("plan-delta", message.Type);
		Assert.Equal("- inspect", message.Text);
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
		// Codex reports an interrupted turn as turn/completed with status "interrupted" — no separate method exists.
		using var interrupted = JsonDocument.Parse(
			"""{"method":"turn/completed","params":{"threadId":"thread_1","turn":{"id":"turn_1","status":"interrupted"}}}""");
		using var status = JsonDocument.Parse(
			"""{"method":"thread/status/changed","params":{"threadId":"thread_1","status":""}}""");

		Assert.Equal("turn-completed", CodexPaneMessages.FromNotification("turn/completed", "thread_1", turn.RootElement)?.Type);
		var interruptedMessage = CodexPaneMessages.FromNotification("turn/completed", "thread_1", interrupted.RootElement);
		Assert.Equal("turn-completed", interruptedMessage?.Type);
		Assert.Equal("interrupted", interruptedMessage?.Status);
		Assert.Null(CodexPaneMessages.FromNotification("thread/status/changed", "thread_1", status.RootElement));
	}

	[Fact]
	public void FromNotification_MapsTerminalTurnErrorToVisiblePaneError() {
		using var doc = JsonDocument.Parse(
			"""{"method":"error","params":{"threadId":"thread_1","turnId":"turn_1","willRetry":false,"error":{"message":"You have no weighted tokens left","codexErrorInfo":"usageLimitExceeded","additionalDetails":null}}}""");

		var message = CodexPaneMessages.FromNotification("error", "thread_1", doc.RootElement);

		Assert.NotNull(message);
		Assert.Equal("error", message.Type);
		Assert.Equal("Codex usage limit reached", message.Summary);
		Assert.Equal("You have no weighted tokens left", message.Text);
		Assert.Equal("failed", message.Status);
	}

	[Fact]
	public void FromNotification_PreservesFailedTurnErrorAsFallback() {
		using var doc = JsonDocument.Parse(
			"""{"method":"turn/completed","params":{"threadId":"thread_1","turn":{"id":"turn_1","status":"failed","error":{"message":"The conversation exceeded the context window","codexErrorInfo":"contextWindowExceeded","additionalDetails":"Start a new task."}}}}""");

		var message = CodexPaneMessages.FromNotification("turn/completed", "thread_1", doc.RootElement);

		Assert.NotNull(message);
		Assert.Equal("Conversation is too long", message.Summary);
		Assert.Equal("The conversation exceeded the context window\nStart a new task.", message.Text);
		Assert.Equal("failed", message.Status);
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

	[Fact]
	public void FromThreadSnapshot_MapsPersistedTurnsThroughThePaneContract() {
		using var doc = JsonDocument.Parse(
			"""{"thread":{"id":"thread_1","turns":[{"id":"turn_1","status":"completed","items":[{"type":"userMessage","id":"user_1","content":[{"type":"text","text":"Fix it","text_elements":[]},{"type":"localImage","path":"/tmp/paste.png"}]},{"type":"commandExecution","id":"command_1","command":"dotnet test","cwd":"/repo","status":"completed","commandActions":[],"aggregatedOutput":"Passed","exitCode":0},{"type":"agentMessage","id":"agent_1","text":"Done"}]}]}}""");

		var messages = CodexPaneMessages.FromThreadSnapshot(doc.RootElement);

		Assert.Collection(
			messages,
			message => {
				Assert.Equal("user-message", message.Type);
				Assert.Equal("Fix it", message.Text);
			},
			message => {
				Assert.Equal("user-image", message.Type);
				Assert.Equal("/tmp/paste.png", message.Text);
			},
			message => {
				Assert.Equal("item-completed", message.Type);
				Assert.Equal("commandExecution", message.ItemType);
				Assert.Equal("Passed", message.Text);
			},
			message => {
				Assert.Equal("item-completed", message.Type);
				Assert.Equal("Done", message.Text);
			},
			message => Assert.Equal("turn-completed", message.Type));
	}

	[Fact]
	public void FromThreadSnapshot_PreservesFailedTurnError() {
		using var doc = JsonDocument.Parse(
			"""{"thread":{"id":"thread_1","turns":[{"id":"turn_1","status":"failed","error":{"message":"You have no weighted tokens left","codexErrorInfo":"usageLimitExceeded","additionalDetails":null},"items":[]}]}}""");

		var message = Assert.Single(CodexPaneMessages.FromThreadSnapshot(doc.RootElement));

		Assert.Equal("turn-completed", message.Type);
		Assert.Equal("failed", message.Status);
		Assert.Equal("Codex usage limit reached", message.Summary);
		Assert.Equal("You have no weighted tokens left", message.Text);
	}
}
