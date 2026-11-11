using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Mcp;
using Weavie.Core.Tests.Workflows;
using Weavie.Core.Theming;
using Weavie.Core.Workflows;
using Weavie.Workflows;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Covers the capability-registry MCP surface — the model-facing server the spawned <c>claude</c>
/// reaches via <c>--mcp-config</c> (a <c>ws://</c> + Bearer entry), distinct from the IDE server.
/// Verifies registry-only tool advertisement, Bearer auth, and the generated port-scoped config file.
/// </summary>
[Collection("Settings")]
public sealed partial class RegistryMcpTests : IDisposable {
	private const string Token = "abcdef0123456789abcdef0123456789";
	private readonly TempDirectory _dir = new("weavie-registry-tests");
	private readonly List<WorkflowRunner> _runners = [];
	private readonly WorkflowRoleStore _roles = new(new InMemoryFileSystem(), "/config/workflow-roles.json");

	public void Dispose() {
		foreach (var runner in _runners) runner.Dispose();
		_dir.Dispose();
	}

	private SettingsStore NewStore() =>
		CoreSettings.CreateStore(_dir.Combine("settings.toml"), enableWatcher: false);

	private SessionWorkflowRegistry NewWorkflows(string personal) => NewWorkflows(personal, new UnusedPort());

	private SessionWorkflowRegistry NewWorkflows(string personal, IWorkflowRunPort port) {
		var catalog = new WorkflowCatalog(_dir.Path, personal, WeaviePaths.RepositoryWorkflowsDir(_dir.Path));
		var runner = new WorkflowRunner(
			catalog, NoWorkflowRoles.Instance, new WorkflowRunStore(new LocalFileSystem(), Path.Combine(_dir.Path, "run.json")),
			new UnusedDecider(), new WorkflowFileReader(new LocalFileSystem(), _dir.Path), port, () => 3, () => true, () => 0.8, _dir.Path, WeavieWorkflows.Vocabulary, id => _dir.Combine("documents", id));
		_runners.Add(runner);
		return new SessionWorkflowRegistry(catalog, runner, _roles);
	}

	[Fact]
	public async Task RegistryMode_AdvertisesOnlySettingsTools() {
		using var store = NewStore();
		await using var server = TestMcp.Server(
			Token, FakeDiffPresenter.AlwaysKeep(), [_dir.Path], "weavie", store, registryMode: true);
		int port = server.Start();
		using var ws = await ConnectBearerAsync(port, Token);

		await SendAsync(ws, Request(1, "tools/list", "{}"));
		using var response = await ReceiveAsync(ws);

		var names = response.RootElement.GetProperty("result").GetProperty("tools")
			.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
		Assert.Contains("listSettings", names);
		Assert.Contains("getSetting", names);
		Assert.Contains("setSetting", names);
		Assert.DoesNotContain("openDiff", names);   // IDE RPC not on the registry server
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task OpenFileReachesTheEditorFromTheRegistryWithoutTheIdeTools(bool focus) {
		using var store = NewStore();
		var presenter = FakeDiffPresenter.AlwaysKeep();
		await using var server = TestMcp.Server(Token, presenter, [_dir.Path], "weavie", store, registryMode: true);
		using var ws = await ConnectBearerAsync(server.Start(), Token);

		await SendAsync(ws, Request(1, "tools/list", "{}"));
		using var list = await ReceiveAsync(ws);
		var names = list.RootElement.GetProperty("result").GetProperty("tools")
			.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
		Assert.Single(names, name => name == McpServer.OpenFileTool);

		await SendAsync(ws, Request(2, "tools/call", JsonSerializer.Serialize(new {
			name = "openFile",
			arguments = new { filePath = "plan.md", preview = true, rendered = true, focus },
		})));
		using var opened = await ReceiveAsync(ws);
		Assert.Equal("FILE_OPENED", ToolText(opened));
		Assert.Equal([("plan.md", true, true, focus)], presenter.Opened);

		await SendAsync(ws, Request(3, "tools/call", """{"name":"openFile","arguments":{}}"""));
		using var missing = await ReceiveAsync(ws);
		Assert.True(missing.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
	}

	[Fact]
	public async Task RegistryMode_WithIdeTools_ExposesEditorContextOverHttp() {
		using var store = NewStore();
		var editor = new EditorStore();
		editor.SetActive(new ActiveEditor(
			_dir.Combine("a.cs"),
			"csharp",
			"selected",
			new EditorSelection(new EditorPosition(3, 1), new EditorPosition(3, 9), IsEmpty: false)));
		await using var server = TestMcp.Server(
			Token, FakeDiffPresenter.AlwaysKeep(), [_dir.Path], "weavie", store, registryMode: true,
			exposeIdeTools: true, editor: editor);
		int port = server.Start();

		using var listed = await PostHttpAsync(port, Request(1, "tools/list", "{}"));
		using var listDocument = await TestMcp.ReadResponseAsync(listed);
		var names = listDocument.RootElement.GetProperty("result").GetProperty("tools")
			.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
		Assert.Contains("listSettings", names);
		Assert.Contains("openDiff", names);
		Assert.Contains("getCurrentSelection", names);

		using var called = await PostHttpAsync(port, Request(2, "tools/call", "{\"name\":\"getCurrentSelection\",\"arguments\":{}}"));
		using var callDocument = await TestMcp.ReadResponseAsync(called);
		string text = callDocument.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
		using var payload = JsonDocument.Parse(text);
		Assert.True(payload.RootElement.GetProperty("success").GetBoolean());
		Assert.Equal("selected", payload.RootElement.GetProperty("text").GetString());
		Assert.Equal(3, payload.RootElement.GetProperty("selection").GetProperty("start").GetProperty("line").GetInt32());
	}

	[Fact]
	public async Task RegistryMode_BearerAuth_AcceptsCorrect_RejectsWrong() {
		using var store = NewStore();
		await using var server = TestMcp.Server(
			Token, FakeDiffPresenter.AlwaysKeep(), [_dir.Path], "weavie", store, registryMode: true);
		int port = server.Start();

		await Assert.ThrowsAsync<WebSocketException>(() => ConnectBearerAsync(port, "wrong-token"));
		using var ws = await ConnectBearerAsync(port, Token); // correct token connects
		Assert.Equal(WebSocketState.Open, ws.State);
	}

	[Fact]
	public async Task RegistryMode_ThemeTools_AdvertiseQueriesAndEditors_NotVerbs() {
		using var store = NewStore();
		var overrides = new ThemeOverridesStore(new InMemoryFileSystem(), _dir.Combine("theme-overrides.json"));
		await using var server = TestMcp.Server(
			Token, FakeDiffPresenter.AlwaysKeep(), [_dir.Path], "weavie", store, registryMode: true, themeOverrides: overrides);
		int port = server.Start();
		using var ws = await ConnectBearerAsync(port, Token);

		await SendAsync(ws, Request(1, "tools/list", "{}"));
		using var response = await ReceiveAsync(ws);

		var names = response.RootElement.GetProperty("result").GetProperty("tools")
			.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
		// Data-shaped operations stay MCP tools: read-only queries and per-color override editors.
		Assert.Contains("listThemes", names);
		Assert.Contains("describeTheme", names);
		Assert.Contains("setThemeOverride", names);
		Assert.Contains("applyThemeTransform", names);
		Assert.Contains("removeThemeOverride", names);
		// Verb actions are commands (reached via runCommand), not advertised as theme tools.
		Assert.DoesNotContain("installTheme", names);
		Assert.DoesNotContain("selectTheme", names);
		Assert.DoesNotContain("resetTheme", names);
		Assert.DoesNotContain("undoThemeOverride", names);
	}

	[Fact]
	public async Task RegistryMode_SetSetting_OverBearer_Persists() {
		using var store = NewStore();
		await using var server = TestMcp.Server(
			Token, FakeDiffPresenter.AlwaysKeep(), [_dir.Path], "weavie", store, registryMode: true);
		int port = server.Start();
		using var ws = await ConnectBearerAsync(port, Token);

		string shell = OperatingSystem.IsWindows() ? "cmd" : "/bin/sh";
		await SendAsync(ws, Request(2, "tools/call",
			$"{{\"name\":\"setSetting\",\"arguments\":{{\"key\":\"terminal.shell\",\"value\":\"{shell}\"}}}}"));
		using var response = await ReceiveAsync(ws);

		Assert.False(response.RootElement.GetProperty("result").TryGetProperty("isError", out var e) && e.GetBoolean());
		Assert.Equal(shell, store.Resolve("terminal.shell").Value);
	}

	[Fact]
	public async Task ApplyThemeTransform_AmountOfOne_IsAccepted() {
		using var store = NewStore();
		var overrides = new ThemeOverridesStore(new InMemoryFileSystem(), _dir.Combine("theme-overrides.json"));
		await using var server = TestMcp.Server(
			Token, FakeDiffPresenter.AlwaysKeep(), [_dir.Path], "weavie", store, registryMode: true, themeOverrides: overrides);
		int port = server.Start();
		using var ws = await ConnectBearerAsync(port, Token);

		await SendAsync(ws, Request(1, "tools/call",
			"{\"name\":\"applyThemeTransform\",\"arguments\":{\"op\":\"darken\",\"amount\":1}}"));
		using var ok = await ReceiveAsync(ws);
		var okResult = ok.RootElement.GetProperty("result");
		Assert.False(okResult.TryGetProperty("isError", out var e1) && e1.GetBoolean());

		await SendAsync(ws, Request(2, "tools/call",
			"{\"name\":\"applyThemeTransform\",\"arguments\":{\"op\":\"darken\",\"amount\":2}}"));
		using var bad = await ReceiveAsync(ws);
		var badResult = bad.RootElement.GetProperty("result");
		Assert.True(badResult.GetProperty("isError").GetBoolean());
	}

	[Fact]
	public async Task SetThemeOverride_AcceptsKnownFontStyle_RejectsUnknown() {
		using var store = NewStore();
		var overrides = new ThemeOverridesStore(new InMemoryFileSystem(), _dir.Combine("theme-overrides.json"));
		await using var server = TestMcp.Server(
			Token, FakeDiffPresenter.AlwaysKeep(), [_dir.Path], "weavie", store, registryMode: true, themeOverrides: overrides);
		int port = server.Start();
		using var ws = await ConnectBearerAsync(port, Token);

		await SendAsync(ws, Request(1, "tools/call",
			"{\"name\":\"setThemeOverride\",\"arguments\":{\"key\":\"comment\",\"table\":\"tokenColors\",\"fontStyle\":\"italic\"}}"));
		using var ok = await ReceiveAsync(ws);
		Assert.False(ok.RootElement.GetProperty("result").TryGetProperty("isError", out var e1) && e1.GetBoolean());

		await SendAsync(ws, Request(2, "tools/call",
			"{\"name\":\"setThemeOverride\",\"arguments\":{\"key\":\"comment\",\"table\":\"tokenColors\",\"fontStyle\":\"wiggly\"}}"));
		using var bad = await ReceiveAsync(ws);
		Assert.True(bad.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
	}

	[Fact]
	public void RegistryMode_RequiresStore() {
		Assert.Throws<ArgumentNullException>(() => TestMcp.Server(
			Token, FakeDiffPresenter.AlwaysKeep(), [_dir.Path], "weavie", settings: null, registryMode: true));
	}

	[Fact]
	public void RegistryMode_WithIdeTools_RequiresEditorStore() {
		using var store = NewStore();
		Assert.Throws<ArgumentNullException>(() => TestMcp.Server(
			Token, FakeDiffPresenter.AlwaysKeep(), [_dir.Path], "weavie", store, registryMode: true, exposeIdeTools: true));
	}

	[Fact]
	public async Task ClaudeIntegration_AttachesTheTurnContextToSubmittedPrompts() {
		using var store = NewStore();
		await using var registry = TestMcp.Registry(Token, FakeDiffPresenter.AlwaysKeep(), [_dir.Path], store);
		await using var ide = new Weavie.Core.Agents.Claude.ClaudeIdeIntegration(
			registry,
			FakeDiffPresenter.AlwaysKeep(),
			[_dir.Path],
			"weavie",
			store,
			new EditorStore(),
			new HostRuntimeInfo(HostTransport.Local, Managed: false, "test"),
			new FixedTurnContext("[Weavie workflow] stage build"),
			_ => Weavie.Core.Agents.AgentEventFeedback.None,
			(_, _) => { });

		using var client = new System.IO.Pipes.NamedPipeClientStream(
			".", Weavie.Core.Hooks.HookProtocol.PipeName(ide.Port), System.IO.Pipes.PipeDirection.InOut,
			System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
		await client.ConnectAsync(5000);
		await Weavie.Core.Hooks.HookProtocol.WriteFramedAsync(
			client, Encoding.UTF8.GetBytes("""{"hook_event_name":"UserPromptSubmit","session_id":"s","prompt":"go"}"""), CancellationToken.None);
		byte[] reply = await Weavie.Core.Hooks.HookProtocol.ReadFramedAsync(client, CancellationToken.None) ?? [];

		using var output = JsonDocument.Parse(reply);
		Assert.Equal("[Weavie workflow] stage build",
			output.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString());
	}

	[Fact]
	public async Task ClaudeIntegration_WritesPortScopedRegistryConfig() {
		using var store = NewStore();
		await using var registry = TestMcp.Registry(Token, FakeDiffPresenter.AlwaysKeep(), [_dir.Path], store);
		await using var ide = new Weavie.Core.Agents.Claude.ClaudeIdeIntegration(
			registry,
			FakeDiffPresenter.AlwaysKeep(),
			[_dir.Path],
			"weavie",
			store,
			new EditorStore(),
			new HostRuntimeInfo(HostTransport.Local, Managed: false, "test"),
			Weavie.Core.Agents.AgentTurnContext.None,
			_ => Weavie.Core.Agents.AgentEventFeedback.None,
			(_, _) => { });

		string path = ide.WriteMcpConfigFile();
		Assert.Contains(registry.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), Path.GetFileName(path), StringComparison.Ordinal);

		using var doc = JsonDocument.Parse(File.ReadAllText(path));
		var weavie = doc.RootElement.GetProperty("mcpServers").GetProperty("weavie");
		Assert.Equal("ws", weavie.GetProperty("type").GetString());
		Assert.Equal($"ws://127.0.0.1:{registry.Port}", weavie.GetProperty("url").GetString());
		Assert.Equal($"Bearer {Token}", weavie.GetProperty("headers").GetProperty("Authorization").GetString());
	}

	[Fact]
	public async Task RegistryMode_Initialize_AdvertisesPromptsCapability() {
		using var store = NewStore();
		await using var server = TestMcp.Server(
			Token, FakeDiffPresenter.AlwaysKeep(), [_dir.Path], "weavie", store, registryMode: true);
		int port = server.Start();
		using var ws = await ConnectBearerAsync(port, Token);

		await SendAsync(ws, Request(1, "initialize", "{\"protocolVersion\":\"2025-03-26\"}"));
		using var response = await ReceiveAsync(ws);

		Assert.True(response.RootElement.GetProperty("result").GetProperty("capabilities").TryGetProperty("prompts", out _));
	}

	[Fact]
	public async Task RegistryMode_ListsAndGetsWorkspaceSetupPrompt() {
		using var store = NewStore();
		await using var server = TestMcp.Server(
			Token, FakeDiffPresenter.AlwaysKeep(), [_dir.Path], "weavie", store, registryMode: true);
		int port = server.Start();
		using var ws = await ConnectBearerAsync(port, Token);

		await SendAsync(ws, Request(1, "prompts/list", "{}"));
		using var list = await ReceiveAsync(ws);
		var names = list.RootElement.GetProperty("result").GetProperty("prompts")
			.EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList();
		Assert.Contains("setup-workspace", names);

		await SendAsync(ws, Request(2, "prompts/get", "{\"name\":\"setup-workspace\"}"));
		using var get = await ReceiveAsync(ws);
		string text = get.RootElement.GetProperty("result").GetProperty("messages")[0]
			.GetProperty("content").GetProperty("text").GetString()!;
		Assert.Contains("test.profile", text, StringComparison.Ordinal);
		Assert.Contains("worktree.setupCommand", text, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("report-weavie-bug")]
	[InlineData("request-weavie-feature")]
	public async Task ReportingPromptsUseTheSharedCatalogAndIncludePublicationPrivacy(string name) {
		using var store = NewStore();
		await using var server = TestMcp.Server(
			Token, FakeDiffPresenter.AlwaysKeep(), [_dir.Path], "weavie", store, registryMode: true);
		using var ws = await ConnectBearerAsync(server.Start(), Token);
		await SendAsync(ws, Request(1, "prompts/list", "{}"));
		using var list = await ReceiveAsync(ws);
		Assert.Contains(list.RootElement.GetProperty("result").GetProperty("prompts").EnumerateArray(),
			prompt => prompt.GetProperty("name").GetString() == name);
		await SendAsync(ws, Request(2, "prompts/get", JsonSerializer.Serialize(new { name })));
		using var get = await ReceiveAsync(ws);
		var message = Assert.Single(get.RootElement.GetProperty("result").GetProperty("messages").EnumerateArray());
		Assert.Equal("user", message.GetProperty("role").GetString());
		string text = message.GetProperty("content").GetProperty("text").GetString()!;
		Assert.Equal(McpPromptCatalog.Require(name).Text, text);
		Assert.Contains("source code from non-public repositories", text, StringComparison.Ordinal);
		Assert.Contains("before ANY external action", text, StringComparison.Ordinal);
		Assert.Contains("approval of that concrete content before publishing", text, StringComparison.Ordinal);
		Assert.Contains("--repo Kapps/weavie", text, StringComparison.Ordinal);
		int readiness = text.IndexOf("gh auth status --hostname github.com", StringComparison.Ordinal);
		Assert.InRange(readiness, 0, text.IndexOf("Prepare the exact public title", StringComparison.Ordinal));
		await SendAsync(ws, Request(3, "tools/call", JsonSerializer.Serialize(new { name = "runPrompt", arguments = new { name } })));
		using var called = await ReceiveAsync(ws);
		Assert.Equal(text, called.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
	}

	[Fact]
	public async Task RunPromptAdvertisesEveryCatalogPromptAndRejectsUnknownNames() {
		using var store = NewStore();
		await using var server = TestMcp.Server(
			Token, FakeDiffPresenter.AlwaysKeep(), [_dir.Path], "weavie", store, registryMode: true);
		using var ws = await ConnectBearerAsync(server.Start(), Token);
		await SendAsync(ws, Request(1, "tools/list", "{}"));
		using var list = await ReceiveAsync(ws);
		var tool = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
			.Single(t => t.GetProperty("name").GetString() == "runPrompt");
		string description = tool.GetProperty("description").GetString()!;
		var names = tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("name").GetProperty("enum")
			.EnumerateArray().Select(n => n.GetString());
		Assert.Equal(McpPromptCatalog.All.Select(p => p.Name), names);
		Assert.All(McpPromptCatalog.All, p => Assert.Contains($"- {p.Name}: {p.Description}", description, StringComparison.Ordinal));
		Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());

		await SendAsync(ws, Request(2, "tools/call", """{"name":"runPrompt","arguments":{"name":"nope"}}"""));
		using var called = await ReceiveAsync(ws);
		Assert.True(called.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
	}

	[Fact]
	public async Task WorkflowToolsListPublishTheSchemaAndValidateByIdOrText() {
		using var store = NewStore();
		string personal = Path.Combine(_dir.Path, "personal");
		Directory.CreateDirectory(personal);
		File.WriteAllText(Path.Combine(personal, "broken.yaml"), "id: broken\ndescription: d\nstages: []\n");
		await using var server = TestMcp.Server(
			Token, FakeDiffPresenter.AlwaysKeep(), [_dir.Path], "weavie", store, registryMode: true, workflows: NewWorkflows(personal));
		using var ws = await ConnectBearerAsync(server.Start(), Token);

		await SendAsync(ws, Request(1, "tools/list", "{}"));
		using var list = await ReceiveAsync(ws);
		var tools = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
			.Where(t => t.GetProperty("name").GetString() is "listWorkflows" or "getWorkflowSchema" or "validateWorkflow").ToList();
		Assert.Equal(3, tools.Count);
		Assert.All(tools, t => Assert.True(t.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean()));

		_roles.Set("codex-acp", new Dictionary<string, WorkflowRoleAgent> {
			["planner"] = new("codex-acp", "gpt-6-astra", new Dictionary<string, string> { ["reasoning_effort"] = "high" }),
		});
		await SendAsync(ws, Request(2, "tools/call", """{"name":"listWorkflows","arguments":{}}"""));
		using var listed = await ReceiveAsync(ws);
		using var workflows = JsonDocument.Parse(ToolText(listed));
		var folders = workflows.RootElement.GetProperty("folders");
		var specDriven = workflows.RootElement.GetProperty("workflows").EnumerateArray().Single(w => w.GetProperty("id").GetString() == "spec-driven");
		Assert.Equal(["planner", "builder"], specDriven.GetProperty("roles").EnumerateArray().Select(role => role.GetString()));
		var saved = Assert.Single(workflows.RootElement.GetProperty("roles").EnumerateArray());
		Assert.Equal(
			("planner", "codex-acp", "gpt-6-astra", "high"),
			(saved.GetProperty("role").GetString(), saved.GetProperty("provider").GetString(), saved.GetProperty("agent").GetProperty("model").GetString(),
				saved.GetProperty("agent").GetProperty("options").GetProperty("reasoning_effort").GetString()));
		Assert.Equal(personal, folders.GetProperty("personal").GetString());
		Assert.Equal(WeaviePaths.RepositoryWorkflowsDir(_dir.Path), folders.GetProperty("repository").GetString());
		var quickFix = workflows.RootElement.GetProperty("workflows").EnumerateArray().Single(w => w.GetProperty("id").GetString() == "quick-fix");
		Assert.Equal("built-in", quickFix.GetProperty("scope").GetString());
		Assert.True(quickFix.GetProperty("valid").GetBoolean());
		Assert.Empty(quickFix.GetProperty("inputs").EnumerateObject());

		await SendAsync(ws, Request(3, "tools/call", """{"name":"getWorkflowSchema","arguments":{}}"""));
		using var schema = await ReceiveAsync(ws);
		Assert.Equal(WorkflowSchema.Json, ToolText(schema));

		var validate = tools.Single(t => t.GetProperty("name").GetString() == "validateWorkflow");
		Assert.Contains("intentional warnings may be ignored", validate.GetProperty("description").GetString(), StringComparison.Ordinal);
		var complete = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
			.Single(t => t.GetProperty("name").GetString() == "completeStage");
		Assert.Contains("current text of explicitly referenced files", complete.GetProperty("inputSchema").GetProperty("properties")
			.GetProperty("summary").GetProperty("description").GetString(), StringComparison.Ordinal);
		Assert.Equal(
			["id", "text"],
			validate.GetProperty("inputSchema").GetProperty("properties").EnumerateObject().Select(p => p.Name));

		await SendAsync(ws, Request(4, "tools/call", """{"name":"validateWorkflow","arguments":{"id":"broken"}}"""));
		using var invalid = await ReceiveAsync(ws);
		Assert.Contains("expected a non-empty list at stages (line 3, column ", ToolText(invalid), StringComparison.Ordinal);

		await SendAsync(ws, Request(5, "tools/call", """{"name":"validateWorkflow","arguments":{"id":"missing"}}"""));
		using var missing = await ReceiveAsync(ws);
		Assert.True(missing.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
		Assert.Contains("quick-fix", ToolText(missing), StringComparison.Ordinal);

		await SendAsync(ws, Request(6, "tools/call", """{"name":"validateWorkflow","arguments":{"text":"id: draft\ndescription: d\nstages: []\n"}}"""));
		using var badProposal = await ReceiveAsync(ws);
		Assert.Contains("expected a non-empty list at stages", ToolText(badProposal), StringComparison.Ordinal);

		await SendAsync(ws, Request(7, "tools/call", """{"name":"validateWorkflow","arguments":{"text":"id: draft\ndescription: d\nstages:\n  - id: a\n    run: { prompt: go }\n"}}"""));
		using var goodProposal = await ReceiveAsync(ws);
		Assert.Equal("Valid.", ToolText(goodProposal));
		Assert.False(File.Exists(Path.Combine(personal, "draft.yaml")));

		foreach (var (id, arguments) in new[] { (8, "{}"), (9, """{"id":"broken","text":"id: a"}"""), (10, """{"path":"broken.yaml"}"""), (11, """{"id":"broken","path":"/etc/passwd"}"""), (12, """{"id":"broken","text":""}""") }) {
			await SendAsync(ws, Request(id, "tools/call", $$"""{"name":"validateWorkflow","arguments":{{arguments}}}"""));
			using var refused = await ReceiveAsync(ws);
			Assert.True(refused.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
			Assert.Contains("takes exactly one of 'id' or 'text'", ToolText(refused), StringComparison.Ordinal);
		}
	}

	[Fact]
	public async Task ForkWorkflowCopiesIntoAScopeAndReportsEveryRefusal() {
		using var store = NewStore();
		string personal = Path.Combine(_dir.Path, "personal");
		await using var server = TestMcp.Server(
			Token, FakeDiffPresenter.AlwaysKeep(), [_dir.Path], "weavie", store, registryMode: true, workflows: NewWorkflows(personal));
		using var ws = await ConnectBearerAsync(server.Start(), Token);

		await SendAsync(ws, Request(1, "tools/list", "{}"));
		using var list = await ReceiveAsync(ws);
		var fork = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
			.Single(t => t.GetProperty("name").GetString() == "forkWorkflow");
		Assert.False(fork.TryGetProperty("annotations", out _));

		await SendAsync(ws, Request(2, "tools/call", """{"name":"forkWorkflow","arguments":{"id":"quick-fix","scope":"personal"}}"""));
		using var forked = await ReceiveAsync(ws);
		string path = Path.Combine(personal, "quick-fix.yaml");
		Assert.Equal($"Forked quick-fix to {path}.", ToolText(forked));
		Assert.True(File.Exists(path));

		await SendAsync(ws, Request(9, "tools/call", """{"name":"listWorkflows","arguments":{}}"""));
		using var listed = await ReceiveAsync(ws);
		using var workflows = JsonDocument.Parse(ToolText(listed));
		var copies = workflows.RootElement.GetProperty("workflows").EnumerateArray()
			.Where(w => w.GetProperty("id").GetString() == "quick-fix").ToList();
		Assert.False(copies.Single(w => w.GetProperty("scope").GetString() == "personal").TryGetProperty("shadowedBy", out _));
		Assert.Equal("personal", copies.Single(w => w.GetProperty("scope").GetString() == "built-in").GetProperty("shadowedBy").GetString());

		foreach (var (id, arguments, message) in new[] {
			(3, """{"id":"quick-fix","scope":"personal"}""", "would not override it"),
			(4, """{"id":"missing","scope":"personal"}""", "No workflow"),
			(5, """{"id":"quick-fix"}""", "requires 'id' and a 'scope'"),
			(6, """{"scope":"personal"}""", "requires 'id' and a 'scope'"),
			(7, """{"id":"quick-fix","scope":"built-in"}""", "requires 'id' and a 'scope'"),
		}) {
			await SendAsync(ws, Request(id, "tools/call", $$"""{"name":"forkWorkflow","arguments":{{arguments}}}"""));
			using var refused = await ReceiveAsync(ws);
			Assert.True(refused.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
			Assert.Contains(message, ToolText(refused), StringComparison.Ordinal);
		}

		Directory.CreateDirectory(Path.Combine(WeaviePaths.RepositoryWorkflowsDir(_dir.Path), "spec-driven.yaml"));
		await SendAsync(ws, Request(8, "tools/call", """{"name":"forkWorkflow","arguments":{"id":"spec-driven","scope":"repository"}}"""));
		using var blocked = await ReceiveAsync(ws);
		Assert.True(blocked.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
	}

	[Fact]
	public async Task WorkflowToolsAreUnavailableWithoutARegistry() {
		using var store = NewStore();
		await using var server = TestMcp.Server(
			Token, FakeDiffPresenter.AlwaysKeep(), [_dir.Path], "weavie", store, registryMode: true);
		using var ws = await ConnectBearerAsync(server.Start(), Token);
		await SendAsync(ws, Request(1, "tools/call", """{"name":"listWorkflows","arguments":{}}"""));
		using var called = await ReceiveAsync(ws);
		Assert.True(called.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
	}

	private static string ToolText(JsonDocument response) =>
		response.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

	[Fact]
	public async Task RegistryMode_GetUnknownPrompt_Errors() {
		using var store = NewStore();
		await using var server = TestMcp.Server(
			Token, FakeDiffPresenter.AlwaysKeep(), [_dir.Path], "weavie", store, registryMode: true);
		int port = server.Start();
		using var ws = await ConnectBearerAsync(port, Token);

		await SendAsync(ws, Request(1, "prompts/get", "{\"name\":\"nope\"}"));
		using var response = await ReceiveAsync(ws);
		Assert.True(response.RootElement.TryGetProperty("error", out _));
	}

	[Fact]
	public async Task IdeMode_ListsNoPrompts() {
		await using var server = TestMcp.Server(
			Token, FakeDiffPresenter.AlwaysKeep(), [_dir.Path], "weavie", settings: null, registryMode: false);
		int port = server.Start();
		using var ws = await ConnectBearerAsync(port, Token);

		await SendAsync(ws, Request(1, "prompts/list", "{}"));
		using var response = await ReceiveAsync(ws);
		Assert.Empty(response.RootElement.GetProperty("result").GetProperty("prompts").EnumerateArray());
	}

	private static async Task<ClientWebSocket> ConnectBearerAsync(int port, string token) {
		var client = new ClientWebSocket();
		client.Options.SetRequestHeader("Authorization", $"Bearer {token}");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), cts.Token);
		return client;
	}

	private static async Task SendAsync(ClientWebSocket ws, string json) =>
		await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

	private static async Task<JsonDocument> ReceiveAsync(ClientWebSocket ws) {
		byte[] buffer = new byte[64 * 1024];
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		var ms = new MemoryStream();
		WebSocketReceiveResult result;
		do {
			result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
			ms.Write(buffer, 0, result.Count);
		}
		while (!result.EndOfMessage);

		return JsonDocument.Parse(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
	}

	private static string Request(int id, string method, string paramsJson) =>
		$"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\",\"params\":{paramsJson}}}";

	private static Task<HttpResponseMessage> PostHttpAsync(int port, string json) => PostHttpAsync(port, json, Token);

	private static async Task<HttpResponseMessage> PostHttpAsync(int port, string json, string token) {
		using var client = new HttpClient();
		using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/mcp") {
			Content = new StringContent(json, Encoding.UTF8, "application/json"),
		};
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		return await client.SendAsync(request);
	}

	private sealed class UnusedDecider : IWorkflowDecider {
		public Task<WorkflowDecisionResult> DecideAsync(WorkflowDecisionRequest request, CancellationToken ct) =>
			throw new InvalidOperationException("No decision is expected.");
	}

	private sealed class UnusedPort : IWorkflowRunPort {
		public void Kickoff(WorkflowKickoff turn, IReadOnlyList<WorkflowTurnImage> images) => throw new InvalidOperationException("No kickoff is expected.");

		public void Notify(string message) => throw new InvalidOperationException("No notice is expected.");

		public string? Refusal(WorkflowLaunch launch) => null;
	}

	private sealed class FixedTurnContext(string text) : Weavie.Core.Agents.IAgentTurnContext {
		public string Compose() => text;
	}
}
