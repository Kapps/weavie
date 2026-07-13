using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Layout;
using Weavie.Core.Mcp;
using Weavie.Core.Sessions;
using Weavie.Core.Shell;
using Weavie.Core.Theming;
using Weavie.Hosting.Agents.Codex;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed partial class CodexAppServerSessionTests : IDisposable {
	private readonly string _dir = Path.Combine(Path.GetTempPath(), "weavie-codex-session-tests", Guid.NewGuid().ToString("N"));
	private SettingsStore? _settings;

	public CodexAppServerSessionTests() {
		Directory.CreateDirectory(_dir);
		File.WriteAllText(Path.Combine(_dir, "app-server"), FakeServerScript);
	}

	public void Dispose() {
		try {
			Directory.Delete(_dir, recursive: true);
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
	}

	[Fact]
	public async Task Start_DoesNotSurfaceInternalLifecycleCards() {
		var events = new CapturingAgentEventSink();
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(events, messages);

		session.Start();
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "thread-start.json")));

		Assert.DoesNotContain(messages, message => message.Type == "process-started");
		Assert.DoesNotContain(messages, message => message.Type == "thread-ready");
	}

	[Fact]
	public async Task ThreadResume_SendsWeavieDeveloperInstructions() {
		var events = new CapturingAgentEventSink();
		List<AgentPaneMessage> messages = [];
		InMemoryFileSystem fileSystem = new();
		CodexThreadStore threads = new(fileSystem, "/codex-threads.json");
		threads.Adopt(_dir, "thread_saved");
		await using var session = CreateSessionWithThreads(events, messages, threads, fileSystem);

		session.Start();
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "thread-resume.json")));

		using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "thread-resume.json")));
		string instructions = doc.RootElement.GetProperty("params").GetProperty("developerInstructions").GetString() ?? "";
		Assert.Equal("thread_saved", doc.RootElement.GetProperty("params").GetProperty("threadId").GetString());
		Assert.Contains("mcp__weavie__currentSession", instructions, StringComparison.Ordinal);
		Assert.Contains("local (loopback only)", instructions, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ThreadResume_HydratesPersistedConversation() {
		var fileSystem = new InMemoryFileSystem();
		var threads = new CodexThreadStore(fileSystem, "/codex-threads.json");
		threads.Adopt(_dir, "thread_existing");
		File.WriteAllText(Path.Combine(_dir, "resume-with-history"), string.Empty);
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSessionWithThreads(new NullAgentEventSink(), messages, threads, fileSystem);

		session.Start();
		await WaitForAsync(() => messages.Any(message => message.Text == "old answer"));

		Assert.Contains(messages, message => message.Type == "transcript-reset");
		Assert.Contains(messages, message => message.Type == "user-message" && message.Text == "old prompt");
		Assert.Contains(messages, message => message.ItemType == "agentMessage" && message.Text == "old answer");
	}

	[Fact]
	public async Task ThreadResume_HydratesBeforeSubmittingInputQueuedDuringStartup() {
		var fileSystem = new InMemoryFileSystem();
		var threads = new CodexThreadStore(fileSystem, "/codex-threads.json");
		threads.Adopt(_dir, "thread_existing");
		File.WriteAllText(Path.Combine(_dir, "resume-with-history"), string.Empty);
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSessionWithThreads(new NullAgentEventSink(), messages, threads, fileSystem);

		session.Submit(Submission("queued prompt", []));
		session.Start();
		await WaitForAsync(() => messages.Any(message => message.Text == "queued prompt"));

		int history = messages.FindIndex(message => message.Text == "old answer");
		int queued = messages.FindIndex(message => message.Text == "queued prompt");
		Assert.True(history >= 0 && queued > history);
	}

	[Fact]
	public async Task ThreadResume_Rejected_StartsFreshThreadAndClearsSavedMapping() {
		var fileSystem = new InMemoryFileSystem();
		var threads = new CodexThreadStore(fileSystem, "/codex-threads.json");
		threads.Adopt(_dir, "thread_broken");
		File.WriteAllText(Path.Combine(_dir, "resume-fails"), string.Empty);
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSessionWithThreads(new NullAgentEventSink(), messages, threads, fileSystem);

		session.Start();
		await WaitForAsync(() => messages.Any(message => message.Type == "warning"));

		// The stale transcript is dropped and the failure is surfaced with its protocol detail, not hidden.
		Assert.Contains(messages, message => message.Type == "transcript-reset");
		var warning = Assert.Single(messages, message => message.Type == "warning");
		Assert.Contains("started a new one", warning.Summary, StringComparison.Ordinal);
		Assert.Contains("-32603", warning.Text, StringComparison.Ordinal);
		Assert.Contains("failed to read thread", warning.PayloadJson ?? "", StringComparison.Ordinal);
		Assert.DoesNotContain(messages, message => message.Type == "error");

		// The dead mapping is gone, and the fresh thread is live: a prompt starts a turn and re-persists.
		Assert.False(threads.Resolve(_dir).Resume);
		session.Submit(Submission("hi", []));
		await WaitForAsync(() => threads.Resolve(_dir).Resume);
		Assert.Equal("thread_fake", threads.Resolve(_dir).ThreadId);
	}

	[Fact]
	public async Task ThreadResume_Rejected_WhenFreshStartAlsoFails_SurfacesStartErrorAndKeepsMapping() {
		var fileSystem = new InMemoryFileSystem();
		var threads = new CodexThreadStore(fileSystem, "/codex-threads.json");
		threads.Adopt(_dir, "thread_broken");
		File.WriteAllText(Path.Combine(_dir, "resume-fails"), string.Empty);
		File.WriteAllText(Path.Combine(_dir, "start-fails"), string.Empty);
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSessionWithThreads(new NullAgentEventSink(), messages, threads, fileSystem);

		session.Start();
		await WaitForAsync(() => messages.Any(message => message.Type == "error"));

		// The session can't start at all, not just resume: surface the start failure, keep the mapping and
		// the pane transcript so a fixed config resumes the same conversation.
		var error = Assert.Single(messages, message => message.Type == "error");
		Assert.Contains("unknown variant", error.Text, StringComparison.Ordinal);
		Assert.DoesNotContain(messages, message => message.Type == "transcript-reset");
		Assert.True(threads.Resolve(_dir).Resume);
		Assert.Equal("thread_broken", threads.Resolve(_dir).ThreadId);
	}

	[Fact]
	public async Task ThreadId_PersistsOnlyAfterTurnStarts() {
		InMemoryFileSystem fileSystem = new();
		CodexThreadStore threads = new(fileSystem, "/codex-threads.json");
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSessionWithThreads(new NullAgentEventSink(), messages, threads, fileSystem);

		session.Start();
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "thread-start.json")));

		Assert.False(threads.Resolve(_dir).Resume);

		session.Submit(Submission("hi", []));
		await WaitForAsync(() => threads.Resolve(_dir).Resume);

		Assert.Equal("thread_fake", threads.Resolve(_dir).ThreadId);
	}

	[Fact]
	public async Task Submit_WhileFreshTurnStarts_QueuesThenSteers() {
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(new CapturingAgentEventSink(), messages);

		session.Start();
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "thread-start.json")));
		session.Submit(Submission("delay start", []));
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "turn-start-pending.json")));
		session.Submit(Submission("second", []));
		File.WriteAllText(Path.Combine(_dir, "release-turn-start"), string.Empty);
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "turn-steer.json")));

		using var started = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "turn-start.json")));
		using var steered = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "turn-steer.json")));
		Assert.Equal("delay start", started.RootElement.GetProperty("params").GetProperty("input")[0].GetProperty("text").GetString());
		Assert.Equal("second", steered.RootElement.GetProperty("params").GetProperty("input")[0].GetProperty("text").GetString());
	}

	[Fact]
	public async Task Submit_MidTurn_SteersTheActiveTurn() {
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(new CapturingAgentEventSink(), messages);

		session.Start();
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "thread-start.json")));
		session.Submit(Submission("go", []));
		await WaitForAsync(() => messages.Any(message => message.Type == "turn-started"));

		session.Submit(Submission("keep going", []));
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "turn-steer.json")));
		await WaitForAsync(() => messages.Any(message => message.Type == "user-steer" && message.Text == "keep going"));

		using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "turn-steer.json")));
		var parameters = doc.RootElement.GetProperty("params");
		Assert.Equal("turn_fake", parameters.GetProperty("expectedTurnId").GetString());
		Assert.Equal("keep going", parameters.GetProperty("input")[0].GetProperty("text").GetString());
	}

	[Fact]
	public async Task SubagentLifecycle_KeepsThePrimaryTurnActiveAndNarrationVisible() {
		var events = new CapturingAgentEventSink();
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(events, messages);

		session.Start();
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "thread-start.json")));
		session.Submit(Submission("subagent", []));
		await WaitForAsync(() => messages.Any(message => message.Text == "Subagent update"));

		var narration = Assert.Single(messages, message => message.Text == "Subagent update");
		Assert.False(narration.IsPrimaryThread);
		Assert.Single(events.Values.OfType<AgentPromptSubmitted>());
		Assert.Single(events.Values.OfType<AgentSessionStarted>());
		Assert.Empty(events.Values.OfType<AgentTurnStopped>());

		session.Submit(Submission("keep going", []));
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "turn-steer.json")));
		using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "turn-steer.json")));
		Assert.Equal("turn_fake", doc.RootElement.GetProperty("params").GetProperty("expectedTurnId").GetString());
	}

	[Theory]
	[InlineData("server resolves approval", "thread_fake", "turn_fake", true)]
	[InlineData("server resolves subapproval", "thread_sub", "turn_sub", false)]
	public async Task ServerResolvedRequest_ClearsPendingStateWithOriginalProvenance(
		string prompt,
		string threadId,
		string turnId,
		bool primary) {
		var events = new CapturingAgentEventSink();
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(events, messages);

		session.Start();
		await WaitForAsync(() => events.Values.OfType<AgentSessionStarted>().Any());
		session.Submit(Submission(prompt, []));
		await WaitForAsync(() => messages.Any(message => message.Type == "approval-resolved"));

		var resolved = Assert.Single(messages, message => message.Type == "approval-resolved");
		Assert.Equal(threadId, resolved.ThreadId);
		Assert.Equal(turnId, resolved.TurnId);
		Assert.Equal(primary, resolved.IsPrimaryThread);
		Assert.Contains(events.Values, value => value is AgentPermissionRequested);
		Assert.Contains(events.Values, value => value is AgentPermissionResolved { RequiresUserInput: false });

		int resolvedCount = messages.Count(message => message.Type == "approval-resolved");
		session.ResolveApproval("cleanup-1", "accept");
		Assert.Equal(resolvedCount, messages.Count(message => message.Type == "approval-resolved"));
	}

	[Fact]
	public async Task Submit_WhenSteerRejected_SurfacesCodexCodeAndRecoversAsFreshTurn() {
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(new CapturingAgentEventSink(), messages);

		session.Start();
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "thread-start.json")));
		session.Submit(Submission("go", []));
		await WaitForAsync(() => messages.Any(message => message.Type == "turn-started"));

		session.Submit(Submission("stale steer", []));
		// The rejected steer is resent as a fresh turn/start carrying the same input, never dropped.
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "turn-steer.json")));
		await WaitForAsync(() =>
			File.ReadAllText(Path.Combine(_dir, "turn-start.json")).Contains("stale steer", StringComparison.Ordinal));
		await WaitForAsync(() => messages.Any(message => message.Type == "user-message" && message.Text == "stale steer"));

		// The rejection is surfaced with its JSON-RPC code and raw envelope, not hidden or shown as a raw blob.
		var warning = Assert.Single(messages, message => message.Type == "warning");
		Assert.Contains("-32600", warning.Text, StringComparison.Ordinal);
		Assert.Contains("expected active turn", warning.PayloadJson ?? "", StringComparison.Ordinal);
		Assert.DoesNotContain(messages, message => message.Type == "error");
		Assert.DoesNotContain(messages, message => message.Type == "user-steer" && message.Text == "stale steer");
	}

	[Fact]
	public async Task Restart_RetractsPendingApprovalCardsLoudly() {
		var events = new CapturingAgentEventSink();
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(events, messages);

		session.Start();
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "thread-start.json")));

		session.Submit(Submission("approval then crash", []));
		await WaitForAsync(() => messages.Any(message => message.Type == "approval-requested"));
		await WaitForAsync(() => messages.Any(message => message.Type == "approval-resolved" && message.ItemId == "approval-3"));

		Assert.Contains(messages, message =>
			message.Type == "approval-resolved"
			&& message.ItemId == "approval-3"
			&& message.ThreadId == "thread_fake"
			&& message.TurnId == "turn_fake"
			&& message.IsPrimaryThread == true
			&& message.Status == "cancel");
		Assert.Contains(messages, message => message.Type == "warning" && message.ItemId == "approval-3");
		Assert.Contains(events.Values, value => value is AgentPermissionResolved);
	}

	[Fact]
	public async Task ResolveApproval_UnknownRequest_UnwedgesTheCardAndExplains() {
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(new CapturingAgentEventSink(), messages);

		session.Start();
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "thread-start.json")));

		session.ResolveApproval("approval-ghost", "accept");

		Assert.Contains(messages, message =>
			message.Type == "approval-resolved" && message.ItemId == "approval-ghost" && message.Status == "cancel");
		Assert.Contains(messages, message => message.Type == "error");
	}

	[Fact]
	public async Task FileChangeApproval_CardCarriesTheChangedPathsFromTheItem() {
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(new CapturingAgentEventSink(), messages);

		session.Start();
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "thread-start.json")));

		session.Submit(Submission("file approval", []));
		await WaitForAsync(() => messages.Any(message => message.Type == "approval-requested"));

		var card = messages.Single(message => message.Type == "approval-requested");
		Assert.Equal("item/fileChange/requestApproval", card.ItemType);
		Assert.Equal("apply the patch", card.Summary);
		Assert.Equal("src/App.cs, src/Program.cs", card.Text);
	}

	[Fact]
	public async Task FileChangeApproval_UsesTheMatchingThreadAndTurnSummary() {
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(new CapturingAgentEventSink(), messages);

		session.Start();
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "thread-start.json")));
		session.Submit(Submission("file approval collision", []));
		await WaitForAsync(() => messages.Any(message => message.ItemId == "approval-4"));

		var card = Assert.Single(messages, message => message.ItemId == "approval-4");
		Assert.Equal("src/Root.cs", card.Text);
	}

	[Fact]
	public async Task FileChangeTrackingFault_SurfacesErrorAndKeepsThePaneEvent() {
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(new DirectChangeThrowingEventSink(), messages);

		session.Start();
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "thread-start.json")));
		session.Submit(Submission("file approval", []));
		await WaitForAsync(() => messages.Any(message => message.Summary == "Change tracking failed for this file"));
		await WaitForAsync(() => messages.Any(message => message.Type == "approval-requested"));

		Assert.Contains(messages, message => message.ItemId == "item_edit" && message.Category == "edit");
	}

	[Fact]
	public async Task ApprovalRequest_UpdatesSharedStatusEvents() {
		var events = new CapturingAgentEventSink();
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(events, messages);

		session.Start();
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "thread-start.json")));

		session.Submit(Submission("approval", []));
		await WaitForAsync(() => messages.Any(message => message.Type == "approval-requested"));

		Assert.Contains(events.Values, value => value is AgentPermissionRequested);

		session.ResolveApproval("approval-1", "accept");
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "approval-response.json")));

		Assert.Contains(events.Values, value => value is AgentPermissionResolved { RequiresUserInput: false });
		using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "approval-response.json")));
		Assert.Equal("approval-1", doc.RootElement.GetProperty("id").GetString());
		Assert.Equal("accept", doc.RootElement.GetProperty("result").GetProperty("decision").GetString());
		var resolved = Assert.Single(messages, message => message.Type == "approval-resolved");
		Assert.Equal("thread_fake", resolved.ThreadId);
		Assert.Equal("turn_fake", resolved.TurnId);

		int errorCount = messages.Count(message => message.Type == "error");
		session.ResolveApproval("approval-1", "accept");

		Assert.Equal(errorCount, messages.Count(message => message.Type == "error"));
	}

	[Fact]
	public async Task BypassPermissions_UsesFullAccessAndNeverApproval() {
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(new CapturingAgentEventSink(), messages, bypassPermissions: true);

		session.Start();
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "thread-start.json")));

		using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "thread-start.json")));
		var parameters = doc.RootElement.GetProperty("params");
		Assert.Equal("danger-full-access", parameters.GetProperty("sandbox").GetString());
		Assert.Equal("never", parameters.GetProperty("approvalPolicy").GetString());

		session.SetControl("sandbox", "read-only");
		session.SetControl("approvalPolicy", "untrusted");
		Assert.Equal("danger-full-access", session.ControlState.Axes.Single(axis => axis.Id == "sandbox").Value);
		Assert.Equal("never", session.ControlState.Axes.Single(axis => axis.Id == "approvalPolicy").Value);

		session.Submit(Submission("go", []));
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "turn-start.json")));
		using var turn = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "turn-start.json")));
		var turnParameters = turn.RootElement.GetProperty("params");
		Assert.Equal("dangerFullAccess", turnParameters.GetProperty("sandboxPolicy").GetProperty("type").GetString());
		Assert.Equal("never", turnParameters.GetProperty("approvalPolicy").GetString());
	}

	[Fact]
	public async Task BypassPermissions_AutoAcceptsCodexApprovalWithoutPromptCard() {
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(new CapturingAgentEventSink(), messages, bypassPermissions: true);

		session.Start();
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "thread-start.json")));
		session.Submit(Submission("approval", []));
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "approval-response.json")));

		using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "approval-response.json")));
		Assert.Equal("accept", doc.RootElement.GetProperty("result").GetProperty("decision").GetString());
		Assert.DoesNotContain(messages, message => message.Type == "approval-requested");
	}

	[Fact]
	public async Task ThreadStart_SendsWeavieDeveloperInstructions() {
		var events = new CapturingAgentEventSink();
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(events, messages);

		session.Start();
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "thread-start.json")));

		using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "thread-start.json")));
		string instructions = doc.RootElement.GetProperty("params").GetProperty("developerInstructions").GetString()!;
		Assert.Contains("mcp__weavie__currentSession", instructions, StringComparison.Ordinal);
		Assert.Contains("local (loopback only)", instructions, StringComparison.Ordinal);
	}

	[Fact]
	public async Task UnsupportedServerRequest_SurfacesErrorCard() {
		var events = new CapturingAgentEventSink();
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(events, messages);

		session.Start();
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "thread-start.json")));

		session.Submit(Submission("unsupported", []));
		await WaitForAsync(() => messages.Any(message => message.ItemType == "item/tool/call"));

		var error = Assert.Single(messages, message => message.ItemType == "item/tool/call");
		Assert.Equal("error", error.Type);
		Assert.Contains("not supported", error.Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task FailedTurn_SurfacesProviderError() {
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(new CapturingAgentEventSink(), messages);

		session.Start();
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "thread-start.json")));
		session.Submit(Submission("out of tokens", []));
		await WaitForAsync(() => messages.Any(message => message.Type == "error"));

		var error = Assert.Single(messages, message => message.Type == "error");
		Assert.Equal("Codex usage limit reached", error.Summary);
		Assert.Equal("You have no weighted tokens left", error.Text);
		Assert.Equal("failed", error.Status);
	}

	[Fact]
	public async Task AttachImage_SendsLocalImageWithNextPrompt() {
		var events = new CapturingAgentEventSink();
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(events, messages);

		session.Start();
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "thread-start.json")));

		string imagePath = Path.Combine(_dir, "paste-1.png");
		Assert.False(File.Exists(Path.Combine(_dir, "image-turn.json")));
		session.Submit(Submission("describe it", [imagePath]));
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "image-turn.json")));

		using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "image-turn.json")));
		var input = doc.RootElement.GetProperty("params").GetProperty("input");
		Assert.Equal("text", input[0].GetProperty("type").GetString());
		Assert.Equal("describe it", input[0].GetProperty("text").GetString());
		Assert.Equal("localImage", input[1].GetProperty("type").GetString());
		Assert.Equal(Path.Combine(_dir, "paste-1.png"), input[1].GetProperty("path").GetString());
		await WaitForAsync(() =>
			messages.Any(message => message.Type == "user-message" && message.Text == "describe it")
			&& messages.Any(message => message.Type == "user-image" && message.Status == "submitted"));
		Assert.Contains(messages, message => message.Type == "user-message" && message.Text == "describe it");
		Assert.Contains(messages, message => message.Type == "user-image" && message.Status == "submitted");
	}

	[Fact]
	public async Task Controls_ExposeModelsAndSkills_AndApplyModelLiveOnNextTurn() {
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(new CapturingAgentEventSink(), messages);
		List<AgentControlState> states = [];
		session.ControlStateChanged += state => states.Add(state);

		session.Start();
		await WaitForAsync(() => session.ControlState.ModelControl.Models.Count > 0);

		var control = session.ControlState;
		var model = control.ModelControl;
		Assert.Equal("gpt-5.5", model.Value); // the catalog default, since codex.model is unset
		Assert.Equal("GPT-5.5 (Medium)", model.ValueLabel); // model + default effort, no Fast
		Assert.Equal(["gpt-5.5", "gpt-5.4-mini"], model.Models.Select(choice => choice.Id));
		Assert.True(model.Models.Single(choice => choice.Id == "gpt-5.5").Current);
		Assert.Contains(control.Axes, axis => axis.Id == "approvalPolicy");
		Assert.Contains(control.Axes, axis => axis.Id == "sandbox");
		Assert.Contains(control.Slash, entry => entry.Name == "model" && entry.CommandId == CoreCommands.SelectModel);
		Assert.Contains(control.Slash, entry => entry.Name == "review-pr" && entry.SkillName == "review-pr");

		session.SetControl("model", "gpt-5.4-mini");
		Assert.Contains(states, state => state.ModelControl.Value == "gpt-5.4-mini");

		session.Submit(Submission("go", []));
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "turn-start.json")));

		using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "turn-start.json")));
		Assert.Equal("gpt-5.4-mini", doc.RootElement.GetProperty("params").GetProperty("model").GetString());
	}

	[Fact]
	public async Task Controls_RememberSelections_ForNewCodexSessions() {
		List<AgentPaneMessage> messages = [];
		await using (var session = CreateSession(new CapturingAgentEventSink(), messages)) {
			session.Start();
			await WaitForAsync(() => session.ControlState.ModelControl.Models.Count > 0);

			session.SetControl("model", "gpt-5.5");
			session.SetControl("effort", "high");
			session.SetControl("serviceTier", "priority");
			session.SetControl("approvalPolicy", "never");
			session.SetControl("sandbox", "read-only");

			Assert.Equal("gpt-5.5", _settings!.RequireString(CodexSettings.Model));
			Assert.Equal("high", _settings.RequireString(CodexSettings.Effort));
			Assert.Equal("priority", _settings.RequireString(CodexSettings.ServiceTier));
			Assert.Equal("never", _settings.RequireString(CodexSettings.ApprovalPolicy));
			Assert.Equal("read-only", _settings.RequireString(CodexSettings.Sandbox));
		}

		File.Delete(Path.Combine(_dir, "thread-start.json"));
		List<AgentPaneMessage> reopenedMessages = [];
		await using var reopened = CreateSession(new CapturingAgentEventSink(), reopenedMessages);

		reopened.Start();
		await WaitForAsync(() => reopened.ControlState.ModelControl.Models.Count > 0);

		var control = reopened.ControlState;
		var current = CurrentModel(control);
		Assert.Equal("gpt-5.5", current.Id);
		Assert.Equal("high", current.Effort);
		Assert.True(current.FastOn);
		Assert.Equal("never", control.Axes.Single(axis => axis.Id == "approvalPolicy").Value);
		Assert.Equal("read-only", control.Axes.Single(axis => axis.Id == "sandbox").Value);

		using (var thread = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "thread-start.json")))) {
			var parameters = thread.RootElement.GetProperty("params");
			Assert.Equal("gpt-5.5", parameters.GetProperty("model").GetString());
			Assert.Equal("never", parameters.GetProperty("approvalPolicy").GetString());
			Assert.Equal("read-only", parameters.GetProperty("sandbox").GetString());
		}

		reopened.Submit(Submission("go", []));
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "turn-start.json")));
		using var turn = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "turn-start.json")));
		var turnParameters = turn.RootElement.GetProperty("params");
		Assert.Equal("high", turnParameters.GetProperty("effort").GetString());
		Assert.Equal("priority", turnParameters.GetProperty("serviceTier").GetString());
	}

	[Fact]
	public async Task SetControl_WhenSettingsAreMalformed_SurfacesErrorAndKeepsControl() {
		File.WriteAllText(Path.Combine(_dir, "settings.toml"), "codex = [");
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(new CapturingAgentEventSink(), messages);

		session.Start();
		await WaitForAsync(() => session.ControlState.ModelControl.Models.Count > 0);
		session.SetControl("sandbox", "read-only");
		await WaitForAsync(() => messages.Any(message => message.Type == "error"));

		Assert.Contains(messages, message =>
			message.Type == "error" && message.Text!.Contains("settings.toml has TOML parse errors", StringComparison.Ordinal));
		Assert.Equal("workspace-write", session.ControlState.Axes.Single(axis => axis.Id == "sandbox").Value);
	}

	[Fact]
	public async Task Controls_ExposeEffortAndFast_DerivedFromCurrentModel() {
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(new CapturingAgentEventSink(), messages);

		session.Start();
		await WaitForAsync(() => session.ControlState.ModelControl.Models.Count > 0);

		var control = session.ControlState;
		var current = CurrentModel(control);
		Assert.Equal("medium", current.Effort); // gpt-5.5 default reasoning effort
		Assert.Equal(["low", "medium", "high"], current.Efforts.Select(option => option.Id));
		Assert.Equal("priority", current.FastTier);
		Assert.False(current.FastOn); // off by default

		// The non-current model carries its own efforts and no Fast tier.
		var miniChoice = control.ModelControl.Models.Single(choice => choice.Id == "gpt-5.4-mini");
		Assert.False(miniChoice.Current);
		Assert.Equal("", miniChoice.FastTier);
		Assert.Equal("low", miniChoice.Effort); // mini's default

		Assert.Contains(control.Slash, entry => entry.Name == "effort" && entry.CommandId == CoreCommands.SelectEffort);
		Assert.Contains(control.Slash, entry => entry.Name == "fast" && entry.CommandId == CoreCommands.ToggleFastMode);
	}

	[Fact]
	public async Task Controls_ModelSwitchToUnsupported_ResetsEffort_AndHidesFast() {
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(new CapturingAgentEventSink(), messages);

		session.Start();
		await WaitForAsync(() => session.ControlState.ModelControl.Models.Count > 0);

		session.SetControl("effort", "high");
		session.SetControl("serviceTier", "priority");
		var before = CurrentModel(session.ControlState);
		Assert.Equal("high", before.Effort);
		Assert.True(before.FastOn);

		// gpt-5.4-mini supports neither "high" nor any service tier: the stale effort resets to the mini default and
		// the Fast option disappears entirely.
		session.SetControl("model", "gpt-5.4-mini");
		var control = session.ControlState;
		var after = CurrentModel(control);
		Assert.Equal("gpt-5.4-mini", after.Id);
		Assert.Equal("low", after.Effort);
		Assert.Equal("", after.FastTier);
		Assert.False(after.FastOn);
		Assert.DoesNotContain(control.Slash, entry => entry.Name == "fast");
	}

	[Fact]
	public async Task Submit_SendsEffortAndServiceTier_OnTurnStart_ButNotThreadStart() {
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(new CapturingAgentEventSink(), messages);

		session.Start();
		await WaitForAsync(() => session.ControlState.ModelControl.Models.Count > 0);

		// thread/start carries no effort/serviceTier (the schema forbids effort there); they ride turn/start only.
		using (var threadDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "thread-start.json")))) {
			var threadParams = threadDoc.RootElement.GetProperty("params");
			Assert.False(threadParams.TryGetProperty("effort", out _));
			Assert.False(threadParams.TryGetProperty("serviceTier", out _));
		}

		session.SetControl("effort", "high");
		session.SetControl("serviceTier", "priority");
		session.Submit(Submission("go", []));
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "turn-start.json")));

		using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "turn-start.json")));
		var turnParams = doc.RootElement.GetProperty("params");
		Assert.Equal("high", turnParams.GetProperty("effort").GetString());
		Assert.Equal("priority", turnParams.GetProperty("serviceTier").GetString());
	}

	[Fact]
	public async Task Submit_WithFastOff_SendsNullServiceTierToClearIt() {
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(new CapturingAgentEventSink(), messages);

		session.Start();
		await WaitForAsync(() => session.ControlState.ModelControl.Models.Count > 0);

		session.SetControl("serviceTier", "standard"); // Fast off explicitly
		Assert.Equal("standard", _settings!.RequireString(CodexSettings.ServiceTier));
		session.Submit(Submission("go", []));
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "turn-start.json")));

		using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "turn-start.json")));
		Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("params").GetProperty("serviceTier").ValueKind);
	}

	[Fact]
	public async Task GlobalEffortAndTierSettings_AreScopedToModelsThatSupportThem() {
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(new CapturingAgentEventSink(), messages);

		session.Start();
		await WaitForAsync(() => session.ControlState.ModelControl.Models.Count > 0);

		// Global defaults that gpt-5.4-mini supports neither: it has no service tier and no "high" effort.
		_settings!.Set("codex.serviceTier", JsonDocument.Parse("\"priority\"").RootElement);
		_settings!.Set("codex.effort", JsonDocument.Parse("\"high\"").RootElement);
		session.SetControl("model", "gpt-5.4-mini");

		session.Submit(Submission("go", []));
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "turn-start.json")));

		using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "turn-start.json")));
		var turnParams = doc.RootElement.GetProperty("params");
		// Neither the unsupported tier nor the unsupported effort reaches Codex; the model uses its own defaults.
		Assert.False(turnParams.TryGetProperty("serviceTier", out _));
		Assert.False(turnParams.TryGetProperty("effort", out _));
	}

	[Fact]
	public async Task GlobalEffortSetting_ValidOnNoModel_PassesThroughToSurfaceLoudly() {
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(new CapturingAgentEventSink(), messages);

		session.Start();
		await WaitForAsync(() => session.ControlState.ModelControl.Models.Count > 0);

		// A value no model in the catalog offers is a typo, not a per-model gap: send it so Codex rejects it loudly
		// instead of silently swallowing the misconfiguration.
		_settings!.Set("codex.effort", JsonDocument.Parse("\"bogus\"").RootElement);
		session.Submit(Submission("go", []));
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "turn-start.json")));

		using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "turn-start.json")));
		Assert.Equal("bogus", doc.RootElement.GetProperty("params").GetProperty("effort").GetString());
	}

	[Fact]
	public async Task SetControl_RejectsEffortUnsupportedByModel() {
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(new CapturingAgentEventSink(), messages);

		session.Start();
		await WaitForAsync(() => session.ControlState.ModelControl.Models.Count > 0);

		session.SetControl("effort", "ultra"); // gpt-5.5 does not offer ultra
		await WaitForAsync(() => messages.Any(message => message.Type == "error"));

		Assert.Contains(messages, message => message.Type == "error" && message.Text!.Contains("effort", StringComparison.Ordinal));
		Assert.Equal("medium", CurrentModel(session.ControlState).Effort);
	}

	private static AgentModelChoice CurrentModel(AgentControlState state) =>
		state.ModelControl.Models.Single(model => model.Current);

	[Fact]
	public async Task Submit_WithStagedSkill_SendsResolvedSkillInputItem() {
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(new CapturingAgentEventSink(), messages);

		session.Start();
		await WaitForAsync(() => session.ControlState.Slash.Any(entry => entry.SkillName == "review-pr"));

		session.Submit(Submission("look at the PR", [], ["review-pr"]));
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "turn-start.json")));
		await WaitForAsync(() => messages.Any(message =>
			message.Type == "user-message"
			&& message.Text!.Contains("review-pr", StringComparison.Ordinal)));

		using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "turn-start.json")));
		var input = doc.RootElement.GetProperty("params").GetProperty("input");
		var skill = input.EnumerateArray().Single(item => item.GetProperty("type").GetString() == "skill");
		Assert.Equal("review-pr", skill.GetProperty("name").GetString());
		// The web submitted only the name; the path is resolved server-side from the known skill list.
		Assert.False(string.IsNullOrEmpty(skill.GetProperty("path").GetString()));
		Assert.Contains(messages, message => message.Type == "user-message" && message.Text!.Contains("review-pr", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Submit_SkillOnly_WhenSkillUnavailable_SurfacesErrorAndSendsNothing() {
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(new CapturingAgentEventSink(), messages);

		session.Start();
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "thread-start.json")));

		session.Submit(Submission("", [], ["ghost-skill"]));
		await WaitForAsync(() => messages.Any(message => message.Type == "error"));

		Assert.Contains(messages, message =>
			message.Type == "error" && message.Text!.Contains("no longer available", StringComparison.Ordinal));
		Assert.False(File.Exists(Path.Combine(_dir, "turn-start.json")));
	}

	[Fact]
	public async Task SetControl_RejectsUnknownValue_WithoutChangingState() {
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(new CapturingAgentEventSink(), messages);

		session.Start();
		await WaitForAsync(() => session.ControlState.ModelControl.Models.Count > 0);

		session.SetControl("sandbox", "not-a-mode");
		await WaitForAsync(() => messages.Any(message => message.Type == "error"));

		Assert.Contains(messages, message => message.Type == "error" && message.Text!.Contains("sandbox", StringComparison.Ordinal));
		Assert.Equal("workspace-write", session.ControlState.Axes.Single(axis => axis.Id == "sandbox").Value);
	}

	private static AgentTurnSubmission Submission(string text, IReadOnlyList<string> imagePaths) =>
		Submission(text, imagePaths, []);

	private static AgentTurnSubmission Submission(string text, IReadOnlyList<string> imagePaths, IReadOnlyList<string> skills) => new() {
		Id = Guid.NewGuid().ToString("n"),
		Text = text,
		Attachments = [.. imagePaths.Select((path, index) => new AgentInputAttachment {
			Id = $"image-{index}",
			Path = path,
			Mime = "image/png",
		})],
		Skills = skills,
	};

	private CodexAppServerSession CreateSession(
		IAgentEventSink events,
		List<AgentPaneMessage> messages,
		bool bypassPermissions = false) {
		InMemoryFileSystem fileSystem = new();
		return CreateSessionWithThreads(
			events,
			messages,
			new CodexThreadStore(fileSystem, "/codex-threads.json"),
			fileSystem,
			bypassPermissions);
	}

	private CodexAppServerSession CreateSessionWithThreads(
		IAgentEventSink events,
		List<AgentPaneMessage> messages,
		CodexThreadStore threads,
		InMemoryFileSystem fileSystem,
		bool bypassPermissions = false) {
		var settings = CoreSettings.CreateStore(Path.Combine(_dir, "settings.toml"), enableWatcher: false);
		_settings = settings;
		if (bypassPermissions) {
			settings.Set("claude.allowAllTools", JsonDocument.Parse("true").RootElement);
		}
		var commandRegistry = CoreCommands.CreateRegistry();
		CapabilityRegistryHost registry = new(
			AgentSessionCredential.Create(),
			FakeDiffPresenter.AlwaysKeep(),
			[_dir],
			"weavie",
			settings,
			new LayoutStore(fileSystem, LayoutPanes.CreateRegistry(), "/layout.json"),
			new EditorStore(),
			exposeIdeTools: true,
			new CommandDispatcher(commandRegistry),
			new KeybindingStore(commandRegistry, Path.Combine(_dir, "keybindings.json"), enableWatcher: false),
			new ThemeOverridesStore(fileSystem, "/theme-overrides.json"),
			() => "slot-1");
		CodexAppServerSession session = new(new AgentSessionContext {
			Settings = settings,
			Workspace = _dir,
			FileSystem = fileSystem,
			Registry = registry,
			DiffPresenter = FakeDiffPresenter.AlwaysKeep(),
			Editor = new EditorStore(),
			Runtime = new HostRuntimeInfo(HostTransport.Local, Managed: false, "test"),
			Events = events,
			CurrentSessionId = () => "slot-1",
		}, threads, TestNode.Command);
		session.PaneMessage += messages.Add;
		return session;
	}

	// Flaked 2026-07-13T01:58Z on ci/test (linux) (ThreadResume_HydratesBeforeSubmittingInputQueuedDuringStartup):
	// https://github.com/Kapps/weavie/actions/runs/29218522631/job/86719007250 — TimeoutException after 5s.
	// The fake app-server is a real spawned Node child process, so its round trips are subject to CI scheduling
	// jitter; production ordering (hydrate-then-flush) is not racy. Widened the bound to 20s to absorb that
	// jitter without hiding a real regression.
	private static async Task WaitForAsync(Func<bool> done) {
		for (int i = 0; i < 800; i++) {
			if (done()) {
				return;
			}

			await Task.Delay(25);
		}

		throw new TimeoutException("Condition was not met within the timeout.");
	}

	private sealed class NullAgentEventSink : IAgentEventSink {
		public AgentEventFeedback Observe(AgentEvent value) => AgentEventFeedback.None;
	}

	private sealed class CapturingAgentEventSink : IAgentEventSink {
		public List<AgentEvent> Values { get; } = [];

		public AgentEventFeedback Observe(AgentEvent value) {
			Values.Add(value);
			return AgentEventFeedback.None;
		}
	}

	private sealed class DirectChangeThrowingEventSink : IAgentEventSink {
		public AgentEventFeedback Observe(AgentEvent value) =>
			value is AgentToolStarting
				? throw new IOException("locked file")
				: AgentEventFeedback.None;
	}

}
