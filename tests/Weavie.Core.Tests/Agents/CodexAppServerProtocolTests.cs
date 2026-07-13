using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Agents.Codex;
using Weavie.Core.Changes;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests.Agents;

/// <summary>Codex app-server JSON-RPC is built directly, without SDK or TUI coupling.</summary>
public sealed class CodexAppServerProtocolTests {
	[Fact]
	public void Initialize_IdentifiesWeavie_AndEnablesExperimentalApi() {
		using var doc = JsonDocument.Parse(CodexAppServerProtocol.Initialize(7, "1.2.3"));

		Assert.Equal("initialize", doc.RootElement.GetProperty("method").GetString());
		Assert.Equal(7, doc.RootElement.GetProperty("id").GetInt64());
		var parameters = doc.RootElement.GetProperty("params");
		Assert.Equal("weavie", parameters.GetProperty("clientInfo").GetProperty("name").GetString());
		Assert.True(parameters.GetProperty("capabilities").GetProperty("experimentalApi").GetBoolean());
		Assert.True(parameters.GetProperty("capabilities").GetProperty("mcpServerOpenaiFormElicitation").GetBoolean());
		var optOut = parameters.GetProperty("capabilities").GetProperty("optOutNotificationMethods");
		Assert.Contains(optOut.EnumerateArray(), value => value.GetString() == "hook/started");
		Assert.DoesNotContain(optOut.EnumerateArray(), value => value.GetString() == "item/agentMessage/delta");
	}

	[Fact]
	public void ThreadStart_OmitsEmptyModel() {
		using var doc = JsonDocument.Parse(CodexAppServerProtocol.ThreadStart(
			8, "", "/repo", "workspace-write", "on-request", "use weavie tools"));
		var parameters = doc.RootElement.GetProperty("params");

		Assert.Equal("thread/start", doc.RootElement.GetProperty("method").GetString());
		Assert.Equal("/repo", parameters.GetProperty("cwd").GetString());
		Assert.Equal("workspace-write", parameters.GetProperty("sandbox").GetString());
		Assert.Equal("on-request", parameters.GetProperty("approvalPolicy").GetString());
		Assert.Equal("use weavie tools", parameters.GetProperty("developerInstructions").GetString());
		Assert.False(parameters.TryGetProperty("model", out _));
	}

	[Fact]
	public void ThreadResume_CarriesWorkspacePolicyAndOptionalModel() {
		using var doc = JsonDocument.Parse(CodexAppServerProtocol.ThreadResume(
			12, "thr_1", "gpt-5.1-codex", "/repo", "workspace-write", "on-request", "use weavie tools"));
		var parameters = doc.RootElement.GetProperty("params");

		Assert.Equal("thread/resume", doc.RootElement.GetProperty("method").GetString());
		Assert.Equal("thr_1", parameters.GetProperty("threadId").GetString());
		Assert.Equal("/repo", parameters.GetProperty("cwd").GetString());
		Assert.Equal("workspace-write", parameters.GetProperty("sandbox").GetString());
		Assert.Equal("on-request", parameters.GetProperty("approvalPolicy").GetString());
		Assert.Equal("use weavie tools", parameters.GetProperty("developerInstructions").GetString());
		Assert.Equal("gpt-5.1-codex", parameters.GetProperty("model").GetString());
	}

	[Fact]
	public void TurnStart_CarriesThreadWorkspacePolicyAndText_OmittingEmptyModelEffortAndTier() {
		using var doc = JsonDocument.Parse(CodexAppServerProtocol.TurnStart(
			9, "thr_1", "fix it", "/repo", "workspace-write", "on-request", "", "", ""));
		var parameters = doc.RootElement.GetProperty("params");

		Assert.Equal("turn/start", doc.RootElement.GetProperty("method").GetString());
		Assert.Equal("thr_1", parameters.GetProperty("threadId").GetString());
		Assert.Equal("/repo", parameters.GetProperty("cwd").GetString());
		Assert.Equal("on-request", parameters.GetProperty("approvalPolicy").GetString());
		Assert.Equal("workspaceWrite", parameters.GetProperty("sandboxPolicy").GetProperty("type").GetString());
		Assert.Equal("/repo", parameters.GetProperty("sandboxPolicy").GetProperty("writableRoots")[0].GetString());
		Assert.Equal("fix it", parameters.GetProperty("input")[0].GetProperty("text").GetString());
		Assert.False(parameters.TryGetProperty("model", out _));
		Assert.False(parameters.TryGetProperty("effort", out _));
		Assert.False(parameters.TryGetProperty("serviceTier", out _));
	}

	[Fact]
	public void TurnStart_CarriesModelOverride_ForLiveModelChange() {
		using var doc = JsonDocument.Parse(CodexAppServerProtocol.TurnStart(
			9, "thr_1", "fix it", "/repo", "workspace-write", "on-request", "gpt-5.5", "", ""));

		Assert.Equal("gpt-5.5", doc.RootElement.GetProperty("params").GetProperty("model").GetString());
	}

	[Fact]
	public void TurnStart_CarriesEffort_AndClearsServiceTierToNull() {
		using var doc = JsonDocument.Parse(CodexAppServerProtocol.TurnStart(
			9, "thr_1", "fix it", "/repo", "workspace-write", "on-request", "", "high", "standard"));
		var parameters = doc.RootElement.GetProperty("params");

		Assert.Equal("high", parameters.GetProperty("effort").GetString());
		// "standard" clears any tier: it serializes as an explicit JSON null, not the string "standard".
		Assert.Equal(JsonValueKind.Null, parameters.GetProperty("serviceTier").ValueKind);
	}

	[Fact]
	public void TurnStart_CarriesServiceTier_WhenSet() {
		using var doc = JsonDocument.Parse(CodexAppServerProtocol.TurnStart(
			9, "thr_1", "fix it", "/repo", "workspace-write", "on-request", "", "", "priority"));

		Assert.Equal("priority", doc.RootElement.GetProperty("params").GetProperty("serviceTier").GetString());
	}

	[Fact]
	public void TurnStartWithInputs_CarriesTextImageAndSkillItems() {
		using var doc = JsonDocument.Parse(CodexAppServerProtocol.TurnStartWithInputs(
			13, "thr_1", "describe it", ["/tmp/paste-1.png"], [new CodexSkill("review-pr", "/s/review-pr", "Review a PR.")],
			"/repo", "workspace-write", "on-request", "gpt-5.5", "", ""));
		var parameters = doc.RootElement.GetProperty("params");
		var input = parameters.GetProperty("input");

		Assert.Equal("turn/start", doc.RootElement.GetProperty("method").GetString());
		Assert.Equal("gpt-5.5", parameters.GetProperty("model").GetString());
		Assert.Equal("text", input[0].GetProperty("type").GetString());
		Assert.Equal("describe it", input[0].GetProperty("text").GetString());
		Assert.Equal("localImage", input[1].GetProperty("type").GetString());
		Assert.Equal("/tmp/paste-1.png", input[1].GetProperty("path").GetString());
		Assert.Equal("skill", input[2].GetProperty("type").GetString());
		Assert.Equal("review-pr", input[2].GetProperty("name").GetString());
		Assert.Equal("/s/review-pr", input[2].GetProperty("path").GetString());
	}

	[Fact]
	public void ModelList_RequestsNonHiddenModels() {
		using var doc = JsonDocument.Parse(CodexAppServerProtocol.ModelList(3, false));

		Assert.Equal("model/list", doc.RootElement.GetProperty("method").GetString());
		Assert.False(doc.RootElement.GetProperty("params").GetProperty("includeHidden").GetBoolean());
	}

	[Fact]
	public void SkillsList_SendsSessionCwd() {
		using var doc = JsonDocument.Parse(CodexAppServerProtocol.SkillsList(4, "/repo"));

		Assert.Equal("skills/list", doc.RootElement.GetProperty("method").GetString());
		Assert.Equal("/repo", doc.RootElement.GetProperty("params").GetProperty("cwds")[0].GetString());
	}

	[Fact]
	public void TryReadModelCatalog_MapsModelsEffortsAndServiceTiers() {
		using var doc = JsonDocument.Parse(
			"""{"data":[{"id":"gpt-5.5","model":"gpt-5.5","displayName":"GPT-5.5","description":"Frontier model.","hidden":false,"isDefault":true,"defaultReasoningEffort":"medium","supportedReasoningEfforts":[{"reasoningEffort":"low","description":"Fast."},{"reasoningEffort":"medium","description":"Balanced."},{"reasoningEffort":"xhigh","description":"Extra."}],"defaultServiceTier":"","serviceTiers":[{"id":"priority","name":"Fast","description":"1.5x speed."}]},{"id":"gpt-5.4-mini","model":"gpt-5.4-mini","displayName":"GPT-5.4 mini","description":"Fast model.","hidden":false,"isDefault":false,"defaultReasoningEffort":"low","supportedReasoningEfforts":[{"reasoningEffort":"low","description":"Fast."}],"serviceTiers":[]}]}""");

		Assert.True(CodexModelCatalog.TryReadModelCatalog(doc.RootElement, out var models));
		Assert.Equal(["gpt-5.5", "gpt-5.4-mini"], models.Select(model => model.Model.Id));

		var frontier = models[0];
		Assert.True(frontier.IsDefault);
		Assert.Equal("GPT-5.5", frontier.Model.Label);
		Assert.Equal("medium", frontier.DefaultEffort);
		Assert.Equal(["low", "medium", "xhigh"], frontier.Efforts.Select(effort => effort.Id));
		Assert.Equal("X-High", frontier.Efforts[2].Label); // xhigh prettified
		var tier = Assert.Single(frontier.ServiceTiers);
		Assert.Equal("priority", tier.Id);
		Assert.Equal("Fast", tier.Label);

		var mini = models[1];
		Assert.False(mini.IsDefault);
		Assert.Empty(mini.ServiceTiers);
		Assert.Equal(["low"], mini.Efforts.Select(effort => effort.Id));
	}

	[Fact]
	public void TryReadSkills_FlattensEnabledSkillsWithNameAndPath() {
		using var doc = JsonDocument.Parse(
			"""{"data":[{"cwd":"/repo","errors":[],"skills":[{"name":"review-pr","description":"Review a PR.","enabled":true,"path":"/s/review-pr","scope":"repo","interface":{"shortDescription":"Review a pull request."}},{"name":"disabled","description":"x","enabled":false,"path":"/d","scope":"user"}]}]}""");

		Assert.True(CodexAppServerProtocol.TryReadSkills(doc.RootElement, out var skills));
		var skill = Assert.Single(skills);
		Assert.Equal("review-pr", skill.Name);
		Assert.Equal("/s/review-pr", skill.Path);
		Assert.Equal("Review a pull request.", skill.Description);
	}

	[Fact]
	public void TryReadThreadId_ReadsThreadResult() {
		bool ok = CodexAppServerProtocol.TryReadThreadId(
			"""{"id":1,"result":{"thread":{"id":"thr_123"}}}""",
			out string threadId);

		Assert.True(ok);
		Assert.Equal("thr_123", threadId);
	}

	[Fact]
	public void TryAdaptNotification_MapsTurnAndToolEvents() {
		Assert.True(CodexAppServerProtocol.TryAdaptNotification(
			"""{"method":"turn/started","params":{"turn":{"id":"turn_1"}}}""",
			out var turnStarted));
		Assert.IsType<AgentPromptSubmitted>(turnStarted);

		Assert.True(CodexAppServerProtocol.TryAdaptNotification(
			"""{"method":"item/started","params":{"item":{"type":"commandExecution","id":"item_1","status":"inProgress","command":"dotnet test","commandActions":[],"cwd":"/repo"}}}""",
			out var toolStarted));
		var tool = Assert.IsType<AgentToolStarting>(toolStarted);
		// A shell command counts as agent activity but carries no tracked mutation (Weavie doesn't scan for its side-effects).
		Assert.IsType<AgentMutation.None>(tool.Mutation);

		Assert.True(CodexAppServerProtocol.TryAdaptNotification(
			"""{"method":"turn/completed","params":{"turn":{"id":"turn_1"}}}""",
			out var turnCompleted));
		Assert.IsType<AgentTurnStopped>(turnCompleted);
	}

	[Fact]
	public void TurnSteer_RequiresExpectedTurnId() {
		using var doc = JsonDocument.Parse(CodexAppServerProtocol.TurnSteer(10, "thr_1", "turn_1", "more"));
		var parameters = doc.RootElement.GetProperty("params");

		Assert.Equal("turn/steer", doc.RootElement.GetProperty("method").GetString());
		Assert.Equal("turn_1", parameters.GetProperty("expectedTurnId").GetString());
		Assert.Equal("more", parameters.GetProperty("input")[0].GetProperty("text").GetString());
	}

	[Fact]
	public void TurnSteerWithInputs_CarriesTextImageAndSkillInput() {
		using var doc = JsonDocument.Parse(CodexAppServerProtocol.TurnSteerWithInputs(
			14, "thr_1", "turn_1", "describe it", ["/tmp/paste-1.png"], [new CodexSkill("review-pr", "/s/review-pr", "Review a PR.")]));
		var parameters = doc.RootElement.GetProperty("params");
		var input = parameters.GetProperty("input");

		Assert.Equal("turn/steer", doc.RootElement.GetProperty("method").GetString());
		Assert.Equal("turn_1", parameters.GetProperty("expectedTurnId").GetString());
		Assert.Equal("text", input[0].GetProperty("type").GetString());
		Assert.Equal("describe it", input[0].GetProperty("text").GetString());
		Assert.Equal("localImage", input[1].GetProperty("type").GetString());
		Assert.Equal("/tmp/paste-1.png", input[1].GetProperty("path").GetString());
		Assert.Equal("skill", input[2].GetProperty("type").GetString());
		Assert.Equal("review-pr", input[2].GetProperty("name").GetString());
	}

	[Fact]
	public void TurnInterrupt_CarriesTurnId() {
		using var doc = JsonDocument.Parse(CodexAppServerProtocol.TurnInterrupt(11, "thr_1", "turn_1"));

		Assert.Equal("turn/interrupt", doc.RootElement.GetProperty("method").GetString());
		Assert.Equal("turn_1", doc.RootElement.GetProperty("params").GetProperty("turnId").GetString());
	}

	[Fact]
	public void TryAdaptNotification_MapsFileChangeToFileMutation() {
		Assert.True(CodexAppServerProtocol.TryAdaptNotification(
			"""{"method":"item/started","params":{"item":{"type":"fileChange","id":"item_1","status":"inProgress","changes":[{"path":"src/App.cs","diff":"@@","kind":{"type":"update"}}]}}}""",
			out var value));

		var started = Assert.IsType<AgentToolStarting>(value);
		var file = Assert.IsType<AgentMutation.File>(started.Mutation);
		Assert.Equal("src/App.cs", file.Path);
	}

	[Fact]
	public void TryAdaptNotification_MapsFileChangeCompletionToFileMutation() {
		Assert.True(CodexAppServerProtocol.TryAdaptNotification(
			"""{"method":"item/completed","params":{"completedAtMs":1,"threadId":"thread_1","turnId":"turn_1","item":{"type":"fileChange","id":"item_1","status":"completed","changes":[{"path":"src/App.cs","diff":"@@","kind":{"type":"update"}}]}}}""",
			out var value));

		var completed = Assert.IsType<AgentToolCompleted>(value);
		var file = Assert.IsType<AgentMutation.File>(completed.Mutation);
		Assert.Equal("src/App.cs", file.Path);
	}

	[Fact]
	public void TryAdaptNotification_MapsMultiFileChangeToFileSetMutation() {
		Assert.True(CodexAppServerProtocol.TryAdaptNotification(
			"""{"method":"item/completed","params":{"item":{"type":"fileChange","id":"item_1","status":"completed","changes":[{"path":"src/App.cs","diff":"@@","kind":{"type":"update"}},{"path":"src/Other.cs","diff":"@@","kind":{"type":"update"}}]}}}""",
			out var value));

		var completed = Assert.IsType<AgentToolCompleted>(value);
		var files = Assert.IsType<AgentMutation.Files>(completed.Mutation);
		Assert.Equal(["src/App.cs", "src/Other.cs"], files.Items.Select(file => file.Path));
	}

	[Fact]
	public void FileChangeLifecycle_FeedsTurnReviewDiff() {
		string workspace = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "weavie-codex-review"));
		string file = Path.Combine(workspace, "src", "App.cs");
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText(file, "old\n");
		string pathJson = JsonSerializer.Serialize(file);
		var changes = new SessionChangeTracker(
			fileSystem,
			workspace,
			_ => true);
		string startedJson = "{\"method\":\"item/started\",\"params\":{\"item\":{\"type\":\"fileChange\",\"id\":\"item_1\",\"status\":\"inProgress\",\"changes\":[{\"path\":"
			+ pathJson + ",\"diff\":\"@@\",\"kind\":{\"type\":\"update\"}}]}}}";
		string completedJson = "{\"method\":\"item/completed\",\"params\":{\"item\":{\"type\":\"fileChange\",\"id\":\"item_1\",\"status\":\"completed\",\"changes\":[{\"path\":"
			+ pathJson + ",\"diff\":\"@@\",\"kind\":{\"type\":\"update\"}}]}}}";

		Assert.True(CodexAppServerProtocol.TryAdaptNotification(startedJson, out var started));
		changes.Observe(started);
		fileSystem.WriteAllText(file, "old\nnew\n");
		Assert.True(CodexAppServerProtocol.TryAdaptNotification(completedJson, out var completed));
		changes.Observe(completed);

		var turn = Assert.Single(changes.TurnChanges());
		Assert.Equal(file, turn.Path);
		Assert.Equal("old\n", turn.BaselineText);
		Assert.Equal("old\nnew\n", turn.CurrentText);
	}

	[Fact]
	public void CommandExecutionThatDeletesATrackedFile_ReconcilesIt() {
		// A shell command isn't scanned for side-effects, but its completion still reconciles disk deletions —
		// so a file the agent created (a tracked fileChange) and then rm'd leaves the review board.
		string workspace = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "weavie-codex-command-reconcile"));
		string file = Path.Combine(workspace, "created.txt");
		var fileSystem = new InMemoryFileSystem();
		var changes = new SessionChangeTracker(fileSystem, workspace, _ => true);
		string pathJson = JsonSerializer.Serialize(file);
		string editJson = "{\"method\":\"item/completed\",\"params\":{\"item\":{\"type\":\"fileChange\",\"id\":\"item_1\",\"status\":\"completed\",\"changes\":[{\"path\":"
			+ pathJson + ",\"diff\":\"@@\",\"kind\":{\"type\":\"add\"}}]}}}";
		string rmJson = """{"method":"item/completed","params":{"item":{"type":"commandExecution","id":"item_2","status":"completed","command":"rm created.txt","commandActions":[],"cwd":"/repo"}}}""";

		fileSystem.WriteAllText(file, "new\n");
		Assert.True(CodexAppServerProtocol.TryAdaptNotification(editJson, out var edited));
		changes.Observe(edited);
		Assert.Single(changes.TurnChanges());

		fileSystem.DeleteFile(file);
		Assert.True(CodexAppServerProtocol.TryAdaptNotification(rmJson, out var removed));
		changes.Observe(removed);

		Assert.Empty(changes.TurnChanges());
	}

	[Theory]
	[InlineData("commandExecution")]
	[InlineData("mcpToolCall")]
	[InlineData("dynamicToolCall")]
	public void UnstructuredToolLifecycle_NeverEnumeratesTheWorkspace(string type) {
		var fileSystem = new NoEnumerationFileSystem();
		var changes = new SessionChangeTracker(fileSystem, Path.GetFullPath(Path.GetTempPath()), _ => true);
		string startedJson = $"{{\"method\":\"item/started\",\"params\":{{\"item\":{{\"type\":\"{type}\",\"id\":\"item_1\"}}}}}}";
		string completedJson = $"{{\"method\":\"item/completed\",\"params\":{{\"item\":{{\"type\":\"{type}\",\"id\":\"item_1\"}}}}}}";

		Assert.True(CodexAppServerProtocol.TryAdaptNotification(startedJson, out var started));
		changes.Observe(started);
		Assert.True(CodexAppServerProtocol.TryAdaptNotification(completedJson, out var completed));
		changes.Observe(completed);
	}

	private sealed class NoEnumerationFileSystem : IFileSystem {
		private readonly InMemoryFileSystem _inner = new();

		public bool FileExists(string path) => _inner.FileExists(path);
		public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
		public bool TryGetStat(string path, out FileStat stat) => _inner.TryGetStat(path, out stat);
		public IReadOnlyList<DirectoryEntry> EnumerateDirectory(string path) =>
			throw new Xunit.Sdk.XunitException($"Workspace enumeration attempted at {path}");
		public string ReadAllText(string path) => _inner.ReadAllText(path);
		public bool TryReadAllText(string path, out string contents) => _inner.TryReadAllText(path, out contents);
		public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);
		public void WriteAllText(string path, string contents) => _inner.WriteAllText(path, contents);
		public void WriteAllBytes(string path, byte[] contents) => _inner.WriteAllBytes(path, contents);
		public void AppendAllText(string path, string contents) => _inner.AppendAllText(path, contents);
		public void WriteAllTextAtomic(string path, string contents) => _inner.WriteAllTextAtomic(path, contents);
		public void DeleteFile(string path) => _inner.DeleteFile(path);
	}
}
