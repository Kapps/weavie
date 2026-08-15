using Weavie.Core.Agents;
using Weavie.Core.Editor;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpAgentSessionTests {
	[Fact]
	public async Task NativeSession_StopCancelsAuthenticationAndIgnoresLateSuccess() {
		await using var fixture = AcpAgentSessionFixture.CreateHeldAuthenticationAdapter();
		fixture.Session.Start();
		await fixture.WaitForMessageAsync(message => message.Type == "authentication-requested");

		fixture.Session.Authenticate(
			"fake-login",
			new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
		await Wait.UntilAsync(() => File.Exists(Path.Combine(fixture.Workspace, "authentication-started")));
		fixture.Session.Interrupt();
		var cancelled = await fixture.WaitForMessageAsync(message => message.Type == "authentication-resolved");
		await Wait.UntilAsync(() => File.Exists(Path.Combine(fixture.Workspace, "authentication-completed")));
		await Task.Delay(250);

		Assert.Equal("cancelled", cancelled.Status);
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "authentication-resolved"
			&& message.Status == "accepted");
		Assert.DoesNotContain(fixture.Events.Values, value => value is AgentSessionStarted);
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_PersistsOnlyAfterTheFirstSubmittedTurn() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		Assert.Null(fixture.Sessions.Resolve("fake", fixture.Workspace));
		fixture.Submit("hello");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal("fake-session", fixture.Sessions.Resolve("fake", fixture.Workspace));
	}

	[Fact]
	public async Task NativeSession_TerminatesTheAdapterBeforeAProviderCanRunWithoutPersistence() {
		await using var fixture = AcpAgentSessionFixture.CreateWithFailingPersistence(allowAllPermissions: true);
		await fixture.StartAsync();
		string mutation = Path.Combine(fixture.Workspace, "provider-mutation.txt");

		fixture.Submit("persist-probe:" + mutation);
		var failed = await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		await fixture.WaitForMessageAsync(message => message.Type == "error"
			&& message.Text?.Contains("persistence failure", StringComparison.OrdinalIgnoreCase) == true);
		await fixture.Events.WaitForAsync(value => value is AgentProcessChanged {
			Change.State: Weavie.Core.Processes.SupervisorState.Idle,
		});
		await Task.Delay(TimeSpan.FromMilliseconds(500));

		Assert.Equal("failed", failed.Status);
		Assert.False(File.Exists(mutation));
		Assert.DoesNotContain(fixture.Messages, message =>
			message.Text == "persistence failure did not stop the provider");
		Assert.Equal(SessionStatus.Error, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_IgnoresLiveProviderEchoesOfTheSubmittedPrompt() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("echo-user");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Single(fixture.Messages, message => message.Type == "user-message" && message.Text == "echo-user");
	}

	[Fact]
	public async Task NativeSession_MapsControlsCommandsAndRichUpdates() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		var controls = await fixture.StartAsync();

		var model = Assert.Single(controls.Axes, axis => axis.Id == "model");
		Assert.Equal("Alpha", model.ValueLabel);
		Assert.Equal(["Stable", "Preview"], model.Options.Select(option => option.Group));
		Assert.Equal("false", Assert.Single(controls.Axes, axis => axis.Id == "fast").Value);
		Assert.Equal("default", Assert.Single(controls.Axes, axis => axis.Id == "mode").Value);
		Assert.Contains(controls.Slash, command => command.Name == "compact");

		fixture.Submit("rich");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		var messages = fixture.Messages;
		Assert.Contains(messages, message => message.Type == "item-completed"
			&& message.ItemType == "thought" && message.Text == "inspect");
		var edit = Assert.Single(messages, message => message.Type == "item-completed" && message.ItemId == "tool:edit");
		Assert.Equal("sample.txt", Path.GetFileName(Assert.Single(edit.Locations!).Path));
		Assert.Equal("new", Assert.Single(edit.Diffs!).NewText);
		Assert.Contains(messages, message => message.ItemType == "plan"
			&& message.Text!.Contains("[~] Implement", StringComparison.Ordinal));
		var usage = fixture.Session.Snapshot;
		Assert.Equal(new AgentContextWindowUsage(123, 4096), usage.ContextWindow);
		var limit = Assert.Single(usage.Limits);
		Assert.Equal("seven_day", limit.Id);
		Assert.Equal(AgentUsageLimitStatus.Warning, limit.Status);
		Assert.Equal(62, limit.UsedPercent);
		Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(4102444800), limit.ResetsAt);
		Assert.Contains(messages, message => message.Type == "item-completed" && message.Text == "rich response");
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_PreservesOrderedRichToolContent() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("tool-content");
		var tool = await fixture.WaitForMessageAsync(message => message.ItemId == "tool:content"
			&& message.Type == "item-completed");

		Assert.Collection(tool.Content!,
			content => Assert.Equal(("text", "tool text"), (content.Type, content.Text)),
			content => Assert.Equal(("image", "image/png", "aW1hZ2U="),
				(content.Type, content.MediaType, content.MediaData)),
			content => Assert.Equal(("resource_link", "https://example.test/result", "Result"),
				(content.Type, content.ResourceUri, content.Name)),
			content => Assert.Equal(("resource", "file:///result.txt", "embedded text"),
				(content.Type, content.ResourceUri, content.Text)));
	}

	[Fact]
	public async Task NativeSession_AcceptsEmptyFileWritesAndDiffResults() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		string path = Path.Combine(fixture.Workspace, "empty.txt");

		fixture.Submit("fs-empty:" + path);
		await fixture.WaitForMessageAsync(message => message.Text == "fs: ");
		Assert.Equal(string.Empty, await File.ReadAllTextAsync(path));

		fixture.Submit("empty-diff");
		var tool = await fixture.WaitForMessageAsync(message => message.ItemId == "tool:empty-diff"
			&& message.Type == "item-completed");
		Assert.Equal(string.Empty, Assert.Single(tool.Diffs!).NewText);
	}

	[Fact]
	public async Task NativeSession_SerializesRapidControlMutationsInIssueOrder() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Session.SetControl("model", "beta");
		fixture.Session.SetControl("model", "alpha");
		await fixture.WaitForControlsAsync(state => state.Axes.Any(axis => axis.Id == "model" && axis.Value == "beta"));
		var final = await fixture.WaitForControlsAsync(state =>
			state.Axes.Any(axis => axis.Id == "model" && axis.Value == "alpha"));
		fixture.Submit("control-state");
		await fixture.WaitForMessageAsync(message => message.Text == "control state: alpha/default/False");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal("alpha", Assert.Single(final.Axes, axis => axis.Id == "model").Value);
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_RestartsDuringAControlMutationAndAcceptsFreshControls() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Session.SetControl("model", "beta");
		await Wait.UntilAsync(() => File.Exists(Path.Combine(fixture.Workspace, "control-started")));
		var oldStarts = fixture.Events.Values
			.OfType<AgentSessionStarted>()
			.ToHashSet(ReferenceEqualityComparer.Instance);
		fixture.Session.Restart();
		await fixture.Events.WaitForAsync(value => value is AgentSessionStarted started && !oldStarts.Contains(started));
		fixture.Session.SetControl("model", "beta");
		var controls = await fixture.WaitForControlsAsync(state =>
			state.Axes.Any(axis => axis.Id == "model" && axis.Value == "beta"));
		fixture.Submit("control-state");
		await fixture.WaitForMessageAsync(message => message.Text == "control state: beta/default/False");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal("beta", Assert.Single(controls.Axes, axis => axis.Id == "model").Value);
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_DisposeTerminatesANonresponsiveCloseRequest() {
		await using var fixture = AcpAgentSessionFixture.CreateHeldCloseAdapter();
		await fixture.StartAsync();

		await fixture.Session.DisposeAsync();
	}

	[Fact]
	public async Task NativeSession_SurfacesAnAdapterLaunchFailure() {
		var fixture = AcpAgentSessionFixture.CreateNonLaunchingAdapter(out string executable);
		try {
			fixture.Session.Start();
			var error = await fixture.WaitForMessageAsync(message => message.Type == "error");
			await fixture.Events.WaitForAsync(value => value is AgentProcessChanged {
				Change.State: Weavie.Core.Processes.SupervisorState.Idle,
			});

			Assert.Contains("could not start", error.Text, StringComparison.OrdinalIgnoreCase);
			Assert.Single(fixture.Messages, message => message.Type == "error");
			Assert.Single(fixture.Events.Values, value => value is AgentRuntimeFailed);
			Assert.Equal(SessionStatus.Error, fixture.Events.Status.Status);
		} finally {
			await fixture.DisposeAsync();
			File.Delete(executable);
		}
	}

	[Fact]
	public async Task NativeSession_StartsBeforeAnImmediateProtocolFailure() {
		await using var fixture = AcpAgentSessionFixture.CreateImmediatelyMalformedAdapter();
		fixture.Session.Start();
		var error = await fixture.WaitForMessageAsync(message => message.Type == "error");
		await fixture.Events.WaitForAsync(value => value is AgentProcessChanged {
			Change.State: Weavie.Core.Processes.SupervisorState.Idle,
		});

		Assert.Contains("output could not be handled", error.Text, StringComparison.OrdinalIgnoreCase);
		Assert.Single(fixture.Messages, message => message.Type == "error");
		Assert.Single(fixture.Events.Values, value => value is AgentRuntimeFailed);
		Assert.Equal(SessionStatus.Error, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_SteersLiveTurnAndTracksBackgroundSettle() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("hold");
		await fixture.WaitForMessageAsync(message => message.ItemId == "tool:hold" && message.Type == "item-started");
		fixture.Submit("new direction");
		await fixture.WaitForMessageAsync(message => message.Type == "item-completed"
			&& message.Text == "steered: new direction");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "1");
		Assert.Contains(fixture.Messages, message => message.Type == "user-steer" && message.Text == "new direction");

		fixture.Submit("background");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "2");
		var subagent = Assert.Single(fixture.Messages, message => message.ItemId == "tool:subagent"
			&& message.Type == "item-started");
		Assert.Equal(SessionStatus.Waiting, fixture.Events.Status.Status);

		fixture.Submit("finish-background");
		await fixture.WaitForMessageAsync(message => message.Type == "item-completed"
			&& message.Text == "background finished");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "3");
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_AutoAllowsUsingProviderAdvertisedStrongestOption() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("permission");
		await fixture.WaitForMessageAsync(message => message.Type == "item-completed"
			&& message.Text == "permission: allow-always");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "1");

		Assert.DoesNotContain(fixture.Messages, message => message.Type == "approval-requested");
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_ResolvesManualPermissionsAndTypedInput() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: false, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("permission");
		var approval = await fixture.WaitForMessageAsync(message => message.Type == "approval-requested");
		Assert.Equal(SessionStatus.NeedsInput, fixture.Events.Status.Status);
		fixture.Session.ResolvePermission(approval.RequestId!, "allow-once");
		var resolved = await fixture.WaitForMessageAsync(message => message.Type == "approval-resolved");
		Assert.Equal("allowed once", resolved.Status);
		await fixture.WaitForMessageAsync(message => message.Type == "item-completed"
			&& message.Text == "permission: allow-once");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "1");

		fixture.Submit("input");
		var input = await fixture.WaitForMessageAsync(message => message.Type == "input-requested");
		var question = Assert.Single(input.Questions!);
		Assert.Equal("choice", question.Id);
		Assert.Equal(["one", "two"], question.Options.Select(option => option.Value));
		Assert.False(question.AllowsOther);
		fixture.Session.ResolveInput(input.RequestId!, "accept", new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) {
			["choice"] = ["two"],
		});
		await fixture.WaitForMessageAsync(message => message.Type == "item-completed"
			&& message.Text == "input: two");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "2");
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_ExplicitlyCancelsFormAndUrlElicitations() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: false, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("input-cancel");
		var form = await fixture.WaitForMessageAsync(message => message.Type == "input-requested");
		fixture.Session.ResolveInput(form.RequestId!, "cancel", new Dictionary<string, IReadOnlyList<string>>());
		await fixture.WaitForMessageAsync(message => message.Text == "input action: cancel");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "1");

		fixture.Submit("url-input");
		var url = await fixture.WaitForMessageAsync(message => message.Type == "input-requested"
			&& message.ItemType == "url");
		Assert.Equal("https://example.test/login", url.ResourceUri);
		fixture.Session.ResolveInput(url.RequestId!, "decline", new Dictionary<string, IReadOnlyList<string>>());
		await fixture.WaitForMessageAsync(message => message.Text == "URL action: decline");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "2");
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_TreatsNullStableAcpFormOptionsAsAbsent() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: false, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("input-null-options");
		var input = await fixture.WaitForMessageAsync(message => message.Type == "input-requested");
		Assert.Equal(2, input.Questions!.Count);
		Assert.All(input.Questions, question => Assert.Empty(question.Options));
		fixture.Session.ResolveInput(input.RequestId!, "accept", new Dictionary<string, IReadOnlyList<string>> {
			["text"] = ["free"],
			["values"] = ["one", "two"],
		});
		await fixture.WaitForMessageAsync(message => message.Text == "null options: free | one,two");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_AcceptsStableAcpFormSchemaDefaults() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: false, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("input-default-schema");
		var input = await fixture.WaitForMessageAsync(message => message.Type == "input-requested");
		Assert.Empty(input.Questions!);
		fixture.Session.ResolveInput(input.RequestId!, "accept", new Dictionary<string, IReadOnlyList<string>>());
		await fixture.WaitForMessageAsync(message => message.Text == "default schema action: accept");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_AcceptsTypeLessTitledMultiSelectItems() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: false, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("input-titled-array");
		var input = await fixture.WaitForMessageAsync(message => message.Type == "input-requested");
		var question = Assert.Single(input.Questions!);
		Assert.Equal(["one", "two"], question.Options.Select(option => option.Value));
		fixture.Session.ResolveInput(input.RequestId!, "accept", new Dictionary<string, IReadOnlyList<string>> {
			["values"] = ["one", "two"],
		});
		await fixture.WaitForMessageAsync(message => message.Text == "titled array: one,two");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_RequiresExplicitConsentAfterAUrlCompletionNotification() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: false, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("url-input");
		var input = await fixture.WaitForMessageAsync(message => message.Type == "input-requested"
			&& message.ItemType == "url");
		fixture.Submit("complete-url");
		await fixture.WaitForMessageAsync(message => message.Text == "URL completion notification sent");
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "input-resolved"
			&& message.ItemId == input.ItemId);
		Assert.Equal(SessionStatus.NeedsInput, fixture.Events.Status.Status);
		fixture.Session.ResolveInput(
			input.RequestId!,
			"decline",
			new Dictionary<string, IReadOnlyList<string>>());
		var resolved = await fixture.WaitForMessageAsync(message => message.Type == "input-resolved"
			&& message.ItemId == input.ItemId);
		await fixture.WaitForMessageAsync(message => message.Text == "URL action: decline");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal("decline", resolved.Status);
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Theory]
	[InlineData("unsafe-url", "absolute HTTP or HTTPS URL")]
	[InlineData("password-input", "password forms are not supported")]
	public async Task NativeSession_RejectsUnsafeElicitationSurfaces(string prompt, string expectedError) {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: false, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit(prompt);
		var error = await fixture.WaitForMessageAsync(message => message.Type == "error"
			&& message.Text?.Contains(expectedError, StringComparison.OrdinalIgnoreCase) == true);

		Assert.Contains(expectedError, error.Text, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "input-requested");
	}

	[Fact]
	public async Task NativeSession_BridgesFileSystemEditorContextAndImages() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		string active = Path.Combine(fixture.Workspace, "active.cs");
		fixture.Editor.SetActive(new ActiveEditor(
			active,
			"csharp",
			"selected",
			new EditorSelection(new EditorPosition(2, 3), new EditorPosition(2, 11), IsEmpty: false)));

		fixture.Submit("context");
		await fixture.WaitForMessageAsync(message => message.Type == "item-completed"
			&& message.Text == "context:guidance=True;selection=True");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "1");

		string file = Path.Combine(fixture.Workspace, "written.txt");
		fixture.Submit("fs:" + file);
		await fixture.WaitForMessageAsync(message => message.Type == "item-completed"
			&& message.Text == "fs: written through ACP");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "2");
		Assert.Equal("written through ACP", await File.ReadAllTextAsync(file));

		string image = Path.Combine(fixture.Workspace, "image.png");
		await File.WriteAllBytesAsync(image, [1, 2, 3, 4]);
		fixture.Session.Submit(new AgentTurnSubmission {
			Id = "image-submission",
			Text = "image",
			Attachments = [new AgentInputAttachment { Id = "image", Path = image, Mime = "image/png" }],
		});
		await fixture.WaitForMessageAsync(message => message.Type == "item-completed" && message.Text == "image=True");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "3");
	}

	[Fact]
	public async Task NativeSession_SettlesAFilesystemMutationWhenTheWriteFails() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("fs:" + fixture.Workspace);
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Single(fixture.Events.Values.OfType<AgentToolStarting>());
		Assert.Single(fixture.Events.Values.OfType<AgentToolCompleted>());
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_TerminalLaunchFailureReturnsToTheAgentWithoutHanging() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("terminal-failure");
		var completed = await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		var error = await fixture.WaitForMessageAsync(message => message.Type == "error");

		Assert.Equal("failed", completed.Status);
		Assert.Contains("weavie-missing-terminal-command", error.Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task NativeSession_CancelsTheExactTerminalWaitAndKeepsTheConnectionUsable() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("terminal-cancel");
		await fixture.WaitForMessageAsync(message => message.Text == "terminal wait cancelled; connection alive");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_CancelsAClientRequestBeforeItsDispatchRuns() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		await File.WriteAllTextAsync(Path.Combine(fixture.Workspace, "missing.txt"), "still connected");

		fixture.Submit("cancel-before-dispatch");
		await fixture.WaitForMessageAsync(message => message.Text ==
			"cancel-before-dispatch handled: still connected");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_DistinguishesConcurrentNumericAndStringRequestIds() {
		await using var fixture = AcpAgentSessionFixture.CreateMixedRequestIdAdapter();
		await File.WriteAllTextAsync(Path.Combine(fixture.Workspace, "mixed-number.txt"), "numeric");
		await File.WriteAllTextAsync(Path.Combine(fixture.Workspace, "mixed-string.txt"), "string");
		await fixture.StartAsync();

		fixture.Submit("mixed");
		await fixture.WaitForMessageAsync(message => message.Text == "mixed ids: numeric | string");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_WaitsForTerminalOutputToDrainAfterExit() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("terminal-output");
		await fixture.WaitForMessageAsync(message => message.Type == "item-completed"
			&& message.Text == "terminal: stdout=True;stderr=True;exit=0");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_AcceptsNullStableAcpTerminalOptionals() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("terminal-null-optionals");
		await fixture.WaitForMessageAsync(message => message.Type == "item-completed"
			&& message.Text == "terminal: stdout=True;stderr=True;exit=0");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_PromptFailureTerminalizesEveryActiveTool() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("prompt-failure");
		var failed = await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		await fixture.WaitForMessageAsync(message => message.Type == "error");

		Assert.Equal("failed", failed.Status);
		Assert.Contains(fixture.Messages, message => message.ItemId == "tool:foreground-failure"
			&& message.Type == "item-completed" && message.Status == "failed");
		Assert.Contains(fixture.Messages, message => message.ItemId == "tool:subagent"
			&& message.Type == "item-completed" && message.Status == "failed");
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_AllowsThoughtAndAnswerChunksToShareAProviderMessageId() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("shared-message-id");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Contains(fixture.Messages, message => message.ItemType == "thought"
			&& message.Type == "item-completed" && message.Text == "deep thought");
		Assert.Contains(fixture.Messages, message => message.ItemType == "agentMessage"
			&& message.Type == "item-completed" && message.Text == "final answer");
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
	}

	[Fact]
	public async Task NativeSession_RetractsARefusedTurn() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("refusal");
		var completed = await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal("refusal", completed.Status);
		Assert.Contains(fixture.Messages, message => message.Type == "user-message"
			&& !string.IsNullOrEmpty(message.ItemId));
		Assert.Contains(fixture.Messages, message => message.Type == "item-retracted"
			&& message.ItemId == "thought:thought");
		Assert.Contains(fixture.Messages, message => message.Type == "item-retracted"
			&& message.ItemId == "plan:current");
	}

	[Fact]
	public async Task NativeSession_RejectsAMalformedKnownUpdate() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("malformed-update");
		var error = await fixture.WaitForMessageAsync(message => message.Type == "error");

		Assert.Contains("session/update", error.Text, StringComparison.Ordinal);
		Assert.Equal(SessionStatus.Error, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_AcceptsAnInitializeResponseWithoutAgentCapabilities() {
		await using var fixture = AcpAgentSessionFixture.CreateMinimalCapabilitiesAdapter();

		fixture.Session.Start();
		await fixture.Events.WaitForAsync(value => value is AgentSessionStarted);

		fixture.Submit("hello");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
	}

	[Fact]
	public async Task NativeSession_ResetsAnUnresumableTranscriptBeforeCreatingANewSession() {
		await using var fixture = AcpAgentSessionFixture.CreateMinimalCapabilitiesAdapter();
		fixture.Session.Start();
		await fixture.Events.WaitForAsync(value => value is AgentSessionStarted);
		fixture.Submit("hello");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		Assert.NotNull(fixture.Sessions.Resolve("fake", fixture.Workspace));
		var oldStarts = fixture.Events.Values
			.OfType<AgentSessionStarted>()
			.ToHashSet(ReferenceEqualityComparer.Instance);

		fixture.Session.Restart();
		await fixture.WaitForMessageAsync(message => message.Type == "transcript-reset");
		await fixture.Events.WaitForAsync(value => value is AgentSessionStarted started && !oldStarts.Contains(started));

		Assert.Null(fixture.Sessions.Resolve("fake", fixture.Workspace));
		fixture.Submit("context");
		await fixture.WaitForMessageAsync(message => message.Type == "item-completed"
			&& message.Text == "context:guidance=False;selection=False");
		Assert.NotNull(fixture.Sessions.Resolve("fake", fixture.Workspace));
	}

	[Fact]
	public async Task NativeSession_ColdResumesWithoutReplayAndContinuesLocalTurnIds() {
		await using var fixture = AcpAgentSessionFixture.CreateResumeOnlyAdapter("resume-session", 8);

		await fixture.StartAsync();
		fixture.Submit("hello");
		var response = await fixture.WaitForMessageAsync(message => message.Type == "item-completed"
			&& message.Text == "echo: hello");

		Assert.Equal("resume-session", response.ThreadId);
		Assert.Equal("9", response.TurnId);
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "transcript-reset");
		Assert.Equal(9, fixture.Sessions.ResolveTurnNumber("fake", fixture.Workspace));
	}

	[Fact]
	public async Task NativeSession_AgentAuthenticationKeepsTheInitializedProcessAndRetriesSetup() {
		await using var fixture = AcpAgentSessionFixture.CreateAgentAuthenticationAdapter();
		fixture.Session.Start();
		await fixture.WaitForMessageAsync(message => message.Type == "authentication-requested");

		fixture.Session.Authenticate(
			"fake-login",
			new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
		await fixture.WaitForControlsAsync(state => state.Axes.Count > 0);

		fixture.Submit("hello");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		Assert.Single(fixture.Messages, message => message.Type == "authentication-requested");
	}

	[Fact]
	public async Task NativeSession_TerminalAuthenticationRunsTheDeclaredInvocationAndRestarts() {
		await using var fixture = AcpAgentSessionFixture.CreateTerminalAuthenticationAdapter();
		fixture.Session.Start();
		await fixture.WaitForMessageAsync(message => message.Type == "authentication-requested");

		fixture.Session.Authenticate(
			"fake-terminal-login",
			new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
		await fixture.WaitForControlsAsync(state => state.Axes.Count > 0);

		var launch = Assert.IsType<AgentLaunch>(fixture.AuthenticationLaunch);
		Assert.Contains("terminal-login", launch.Arguments);
		Assert.Equal("1", launch.Environment["FAKE_LOGIN"]);
		Assert.Equal(AgentExecutableMode.Direct, launch.ExecutableMode);
		fixture.Submit("hello");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		Assert.Single(fixture.Messages, message => message.Type == "authentication-requested");
	}

	[Fact]
	public async Task NativeSession_ReauthenticatesAndRetriesAPromptOnTheSameSession() {
		await using var fixture = AcpAgentSessionFixture.CreateAgentAuthenticationAdapter();
		fixture.Session.Start();
		await fixture.WaitForMessageAsync(message => message.Type == "authentication-requested");
		fixture.Session.Authenticate(
			"fake-login",
			new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
		await fixture.WaitForControlsAsync(state => state.Axes.Count > 0);

		fixture.Submit("auth-expired");
		await fixture.WaitForMessageAsync(message => message.Type == "authentication-requested"
			&& fixture.Messages.Count(candidate => candidate.Type == "authentication-requested") == 2);
		fixture.Session.Authenticate(
			"fake-login",
			new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
		var completed = await fixture.WaitForMessageAsync(message => message.Type == "turn-completed"
			&& message.Status == "end_turn");

		Assert.Equal("end_turn", completed.Status);
		Assert.Contains(fixture.Messages, message => message.Type == "item-completed"
			&& message.Text == "echo: auth-expired");
	}

	[Fact]
	public async Task NativeSession_InterruptCancelsTheTurnWithoutReportingAnError() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("hold");
		await fixture.WaitForMessageAsync(message => message.ItemId == "tool:hold" && message.Type == "item-started");
		fixture.Session.Interrupt();
		var completed = await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal("cancelled", completed.Status);
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_TerminallyFailsWhenTheAdapterCannotInterruptTheProvider() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("hold-cancel-error");
		await fixture.WaitForMessageAsync(message => message.ItemId == "tool:hold" && message.Type == "item-started");
		fixture.Session.Interrupt();
		var error = await fixture.WaitForMessageAsync(message => message.Type == "error");
		var completed = await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		await fixture.Events.WaitForAsync(value => value is AgentProcessChanged {
			Change.State: Weavie.Core.Processes.SupervisorState.Idle,
		});

		Assert.Contains("ACP agent", error.Text, StringComparison.OrdinalIgnoreCase);
		Assert.Equal("failed", completed.Status);
		Assert.Equal(SessionStatus.Error, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_LoadsTranscriptAndResumesAcrossProcessReplacement() {
		await using var fixture = AcpAgentSessionFixture.Create(
			allowAllPermissions: true,
			persistedSessionId: "replay-session");
		var snapshotTask = fixture.WaitForSnapshotAsync();
		await fixture.StartAsync();
		var snapshot = await snapshotTask;
		Assert.Contains(snapshot, message => message.Type == "user-message" && message.Text == "first persisted prompt");
		Assert.Contains(snapshot, message => message.Type == "user-message" && message.Text == "second persisted prompt");
		Assert.Contains(snapshot, message => message.Type == "item-completed" && message.Text == "persisted transcript");
		var plans = snapshot.Where(message => message.ItemType == "plan").ToArray();
		Assert.Equal(2, plans.Length);
		Assert.Equal(["1", "2"], plans.Select(message => message.TurnId));
		Assert.Contains(plans, message => message.Text!.Contains("first persisted plan", StringComparison.Ordinal));
		Assert.Contains(plans, message => message.Text!.Contains("second persisted plan", StringComparison.Ordinal));
		Assert.Contains(snapshot, message => message.Type == "item-started" && message.ItemId == "tool:replayed-background");
		Assert.DoesNotContain(snapshot, message => message.Text?.Contains("hidden guidance", StringComparison.Ordinal) == true);
		Assert.DoesNotContain(snapshot, message => message.Text?.Contains("hidden selection", StringComparison.Ordinal) == true);
		Assert.All(snapshot, message => Assert.Equal("replay-session", message.ThreadId));
		Assert.Equal(SessionStatus.Waiting, fixture.Events.Status.Status);

		fixture.Session.Restart();
		await fixture.WaitForControlsAsync(state => state.Axes.Any(axis => axis.Id == "model"));
		fixture.Submit("after restart");
		var response = await fixture.WaitForMessageAsync(message => message.Type == "item-completed"
			&& message.Text == "echo: after restart");
		Assert.Equal("3", response.TurnId);
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
	}

	[Fact]
	public async Task NativeSession_RestartsAnActiveTurnWithoutOldGenerationOutput() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("hold");
		await fixture.WaitForMessageAsync(message => message.ItemId == "tool:hold" && message.Type == "item-started");
		fixture.Session.Restart();
		await fixture.WaitForControlsAsync(state => state.Axes.Any(axis => axis.Id == "model"));
		fixture.Submit("after active restart");
		await fixture.WaitForMessageAsync(message => message.Type == "item-completed"
			&& message.Text == "echo: after active restart");
		var completed = await fixture.WaitForMessageAsync(message => message.Type == "turn-completed"
			&& message.Status == "end_turn");

		Assert.Equal("2", completed.TurnId);
		Assert.Single(fixture.Messages, message => message.Type == "turn-completed" && message.TurnId == "1");
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_RestartCannotDeadlockWithAConcurrentProviderUpdate() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("restart-update-race");
		await fixture.WaitForMessageAsync(message => message.ItemId == "tool:restart-update-race"
			&& message.Type == "item-started");

		var block = fixture.Events.BlockNext<AgentTurnStopped>();
		var restart = Task.Run(fixture.Session.Restart);
		await block.Entered.WaitAsync(TimeSpan.FromSeconds(10));
		File.WriteAllText(Path.Combine(fixture.Workspace, "release-restart-update"), string.Empty);
		await Wait.UntilAsync(() => File.Exists(Path.Combine(fixture.Workspace, "restart-update-sent")));
		block.Release();
		await restart.WaitAsync(TimeSpan.FromSeconds(10));
		await fixture.Events.WaitForAsync(value => value is AgentSessionStarted { Source: "restart" });

		fixture.Submit("after concurrent restart");
		await fixture.WaitForMessageAsync(message => message.Text == "echo: after concurrent restart");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "2");

		Assert.DoesNotContain(fixture.Messages, message => message.Text == "stale update from replaced generation");
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_ProviderCrashIsOneVisibleTerminalFailure() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("crash");
		await fixture.WaitForMessageAsync(message => message.Type == "error");
		await fixture.Events.WaitForAsync(value => value is AgentProcessChanged {
			Change.State: Weavie.Core.Processes.SupervisorState.Idle,
		});

		Assert.Single(fixture.Messages, message => message.Type == "error");
		Assert.Equal(SessionStatus.Error, fixture.Events.Status.Status);
		Assert.Single(fixture.Events.Values, value => value is AgentRuntimeFailed);
	}
}
