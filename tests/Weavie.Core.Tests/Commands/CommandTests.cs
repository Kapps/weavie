using System.Text.Json;
using Weavie.Core.Commands;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Command catalog + dispatcher: registration, unknown-id suggestions, Core handler dispatch,
/// web routing through the host invoker, and registration guards.
/// </summary>
public sealed class CommandTests {
	private static CommandRegistry RegistryWith(params CommandDefinition[] definitions) {
		var registry = new CommandRegistry();
		foreach (var definition in definitions) {
			registry.Register(definition);
		}

		return registry;
	}

	private static CommandDefinition Web(string id) =>
		new() { Id = id, Title = id, RunsIn = CommandLocation.Web };

	private static CommandDefinition Core(string id) =>
		new() { Id = id, Title = id, RunsIn = CommandLocation.Core };

	private static CommandDefinition ClientCore(string id) =>
		Core(id) with { Owner = CommandOwner.Client };

	[Fact]
	public void Register_Duplicate_Throws() {
		var registry = RegistryWith(Web("weavie.a"));
		Assert.Throws<InvalidOperationException>(() => registry.Register(Web("weavie.a")));
	}

	[Fact]
	public void Require_Unknown_ThrowsWithSuggestion() {
		var registry = RegistryWith(Web("weavie.pane.focusByIndex"), Web("weavie.diff.toggleLayout"));
		var ex = Assert.Throws<UnknownCommandException>(() => registry.Require("weavie.pane.focus"));
		Assert.Equal("weavie.pane.focus", ex.Id);
		// "focus" leaf matches focusByIndex.
		Assert.Contains("Did you mean", ex.Message, StringComparison.Ordinal);
		Assert.Contains("weavie.pane.focusByIndex", ex.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Invoke_CoreCommand_RunsHandler() {
		var registry = RegistryWith(Core("weavie.terminal.reopen"));
		var dispatcher = new CommandDispatcher(registry);
		string? seenArgs = null;
		dispatcher.RegisterHandler("weavie.terminal.reopen", (args, _) => {
			seenArgs = args;
			return Task.FromResult(CommandResult.Success("reopened"));
		});

		var result = await dispatcher.InvokeAsync("weavie.terminal.reopen", "{\"x\":1}", CancellationToken.None);

		Assert.True(result.Ok);
		Assert.Equal("reopened", result.Message);
		Assert.Equal("{\"x\":1}", seenArgs);
	}

	[Fact]
	public async Task Invoke_CoreCommand_NoHandler_Fails() {
		var dispatcher = new CommandDispatcher(RegistryWith(Core("weavie.terminal.reopen")));
		var result = await dispatcher.InvokeAsync("weavie.terminal.reopen", null, CancellationToken.None);
		Assert.False(result.Ok);
		Assert.Contains("no handler", result.Error!, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task PrepareModelInvocation_ClientCoreCommand_RelaysToPresentationClient() {
		var dispatcher = new CommandDispatcher(RegistryWith(ClientCore("weavie.font.increase")));
		bool localHandlerRan = false;
		dispatcher.RegisterHandler("weavie.font.increase", (_, _) => {
			localHandlerRan = true;
			return Task.FromResult(CommandResult.Success());
		});
		dispatcher.ClientInvoker = (id, args, _) =>
			Task.FromResult(CommandResult.Success($"{id}:{args}"));

		var execution = await dispatcher.PrepareModelInvocationAsync(
			"weavie.font.increase",
			"{\"step\":1}",
			CancellationToken.None);

		Assert.True(execution.Result.Ok);
		Assert.Equal("weavie.font.increase:{\"step\":1}", execution.Result.Message);
		Assert.False(localHandlerRan);
	}

	[Fact]
	public async Task PrepareModelInvocation_ClientCoreCommandWithoutClient_FailsLoudly() {
		var dispatcher = new CommandDispatcher(RegistryWith(ClientCore("weavie.font.increase")));
		dispatcher.RegisterHandler(
			"weavie.font.increase",
			(_, _) => Task.FromResult(CommandResult.Success()));

		var execution = await dispatcher.PrepareModelInvocationAsync(
			"weavie.font.increase",
			null,
			CancellationToken.None);

		Assert.False(execution.Result.Ok);
		Assert.Contains("presentation client", execution.Result.Error, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task PrepareModelInvocation_BackendCoreCommandStillRunsItsHandler() {
		var dispatcher = new CommandDispatcher(RegistryWith(Core("weavie.terminal.reopen")));
		dispatcher.RegisterHandler(
			"weavie.terminal.reopen",
			(_, _) => Task.FromResult(CommandResult.Success("backend")));

		var execution = await dispatcher.PrepareModelInvocationAsync(
			"weavie.terminal.reopen",
			null,
			CancellationToken.None);

		Assert.Equal("backend", execution.Result.Message);
	}

	[Fact]
	public async Task Invoke_Unknown_Throws() {
		var dispatcher = new CommandDispatcher(RegistryWith(Web("weavie.a")));
		await Assert.ThrowsAsync<UnknownCommandException>(
			() => dispatcher.InvokeAsync("weavie.missing", null, CancellationToken.None));
	}

	[Fact]
	public async Task Invoke_WebCommand_NoInvoker_Fails() {
		var dispatcher = new CommandDispatcher(RegistryWith(Web("weavie.diff.toggleLayout")));
		var result = await dispatcher.InvokeAsync("weavie.diff.toggleLayout", null, CancellationToken.None);
		Assert.False(result.Ok);
		Assert.Contains("web", result.Error!, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Invoke_WebCommand_RoutesThroughInvoker() {
		var dispatcher = new CommandDispatcher(RegistryWith(Web("weavie.pane.focusByIndex"))) {
			WebInvoker = (id, argsJson, _) => Task.FromResult(CommandResult.Success($"{id}:{argsJson}")),
		};

		var result = await dispatcher.InvokeAsync("weavie.pane.focusByIndex", "{\"index\":3}", CancellationToken.None);

		Assert.True(result.Ok);
		Assert.Equal("weavie.pane.focusByIndex:{\"index\":3}", result.Message);
	}

	[Fact]
	public async Task ExecutionLanesOrderRelatedCommandsWithoutBlockingAnotherDomain() {
		var registry = CoreCommands.CreateRegistry();
		var dispatcher = new CommandDispatcher(registry);
		var secondThemeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var fontEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		dispatcher.RegisterHandler(
			CoreCommands.InstallThemeFromFile,
			(_, _) => Task.FromResult(CommandResult.Success()));
		dispatcher.RegisterHandler(CoreCommands.ResetTheme, (_, _) => {
			secondThemeEntered.TrySetResult();
			return Task.FromResult(CommandResult.Success());
		});
		dispatcher.RegisterHandler(CoreCommands.IncreaseFontSize, (_, _) => {
			fontEntered.TrySetResult();
			return Task.FromResult(CommandResult.Success());
		});

		var firstTheme = await dispatcher.PrepareAsync(
			CoreCommands.InstallThemeFromFile,
			null,
			CancellationToken.None);
		var secondThemeTask = dispatcher.PrepareAsync(CoreCommands.ResetTheme, null, CancellationToken.None);
		var fontTask = dispatcher.PrepareAsync(CoreCommands.IncreaseFontSize, null, CancellationToken.None);

		await fontEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
		var font = await fontTask;
		Assert.False(secondThemeEntered.Task.IsCompleted);
		await font.CompleteAsync(CancellationToken.None);

		await firstTheme.CompleteAsync(CancellationToken.None);
		var secondTheme = await secondThemeTask.WaitAsync(TimeSpan.FromSeconds(2));
		await secondTheme.CompleteAsync(CancellationToken.None);
		Assert.True(secondThemeEntered.Task.IsCompletedSuccessfully);
	}

	[Fact]
	public async Task CancelledExecutionLaneWaiterDoesNotPoisonLaterCommands() {
		var dispatcher = new CommandDispatcher(CoreCommands.CreateRegistry());
		dispatcher.RegisterHandler(
			CoreCommands.InstallThemeFromFile,
			(_, _) => Task.FromResult(CommandResult.Success()));
		dispatcher.RegisterHandler(
			CoreCommands.ResetTheme,
			(_, _) => Task.FromResult(CommandResult.Success()));

		var first = await dispatcher.PrepareAsync(
			CoreCommands.InstallThemeFromFile,
			null,
			CancellationToken.None);
		using var cancellation = new CancellationTokenSource();
		var cancelled = dispatcher.PrepareAsync(CoreCommands.ResetTheme, null, cancellation.Token);
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
		var later = dispatcher.PrepareAsync(CoreCommands.ResetTheme, null, CancellationToken.None);

		await first.CompleteAsync(CancellationToken.None);
		var execution = await later.WaitAsync(TimeSpan.FromSeconds(2));
		await execution.CompleteAsync(CancellationToken.None);
	}

	[Fact]
	public void RegisterHandler_OnWebCommand_Throws() {
		var dispatcher = new CommandDispatcher(RegistryWith(Web("weavie.diff.toggleLayout")));
		Assert.Throws<InvalidOperationException>(() =>
			dispatcher.RegisterHandler("weavie.diff.toggleLayout", (_, _) => Task.FromResult(CommandResult.Success())));
	}

	[Fact]
	public void RegisterHandler_Duplicate_Throws() {
		var dispatcher = new CommandDispatcher(RegistryWith(Core("weavie.terminal.reopen")));
		dispatcher.RegisterHandler("weavie.terminal.reopen", (_, _) => Task.FromResult(CommandResult.Success()));
		Assert.Throws<InvalidOperationException>(() =>
			dispatcher.RegisterHandler("weavie.terminal.reopen", (_, _) => Task.FromResult(CommandResult.Success())));
	}

	[Fact]
	public void RegisterHandler_Dispose_Unregisters() {
		var dispatcher = new CommandDispatcher(RegistryWith(Core("weavie.terminal.reopen")));
		var handle = dispatcher.RegisterHandler("weavie.terminal.reopen", (_, _) => Task.FromResult(CommandResult.Success()));
		handle.Dispose();
		// Dispose frees the slot, so re-registering succeeds.
		dispatcher.RegisterHandler("weavie.terminal.reopen", (_, _) => Task.FromResult(CommandResult.Success()));
	}

	[Fact]
	public void CoreCommands_Registry_HasNineFocusBindings() {
		var registry = CoreCommands.CreateRegistry();
		var focus = registry.Require(CoreCommands.FocusPaneByIndex);
		Assert.Equal(CommandLocation.Web, focus.RunsIn);
		Assert.False(focus.ShowInPalette);
		Assert.Equal(9, focus.DefaultKeybindings.Count);
		Assert.Equal("ctrl+1", focus.DefaultKeybindings[0].Key);
		Assert.Equal("{\"index\":1}", focus.DefaultKeybindings[0].ArgsJson);
		Assert.Equal(CommandLocation.Core, registry.Require(CoreCommands.ReopenTerminal).RunsIn);
	}

	[Fact]
	public void ShellTerminalCommands_UseFocusedTabBindingsAndOneLifecycleLane() {
		var registry = CoreCommands.CreateRegistry();
		var reopen = registry.Require(CoreCommands.ReopenTerminal);
		var create = registry.Require(CoreCommands.NewTerminal);
		var close = registry.Require(CoreCommands.CloseTerminal);
		var closePrompt = registry.Require(CoreCommands.CloseTerminalPrompt);
		var next = registry.Require(CoreCommands.NextTerminalTab);
		var previous = registry.Require(CoreCommands.PrevTerminalTab);

		Assert.Equal(CommandLocation.Core, create.RunsIn);
		Assert.Equal(CommandLocation.Core, close.RunsIn);
		Assert.Equal(create.ExecutionLane, reopen.ExecutionLane);
		Assert.Equal(create.ExecutionLane, close.ExecutionLane);
		Assert.Equal("ctrl+Shift+t", Assert.Single(create.DefaultKeybindings).Key);
		Assert.Equal("focusedPane == 'terminal:shell'", Assert.Single(create.DefaultKeybindings).When);
		Assert.Equal("ctrl+Shift+w", Assert.Single(closePrompt.DefaultKeybindings).Key);
		Assert.Equal("ctrl+Tab", Assert.Single(next.DefaultKeybindings).Key);
		Assert.Equal("ctrl+Shift+Tab", Assert.Single(previous.DefaultKeybindings).Key);
		Assert.All(
			[closePrompt, next, previous],
			command => Assert.Equal("focusedPane == 'terminal:shell'", command.When));
	}

	[Fact]
	public void TogglePlanMode_UsesNativeShiftTabBinding() {
		var command = CoreCommands.CreateRegistry().Require(CoreCommands.TogglePlanMode);

		Assert.Equal(CommandLocation.Web, command.RunsIn);
		Assert.Equal("shift+tab", Assert.Single(command.DefaultKeybindings).Key);
		Assert.Equal("agentFocused && !agentSlashMenuOpen && !agentControlPickerOpen", command.When);
	}

	[Fact]
	public void ToggleReviewMode_IsBoundAndReviewGated() {
		var command = CoreCommands.CreateRegistry().Require(CoreCommands.ReviewToggleMode);

		Assert.Equal(CommandLocation.Web, command.RunsIn);
		Assert.Equal("$mod+Shift+u", Assert.Single(command.DefaultKeybindings).Key);
		Assert.Equal("!terminalFocused", command.DefaultKeybindings[0].When);
		Assert.Equal("reviewSetActive", command.When);
	}

	[Fact]
	public void AgentJumpToTurn_RequiresANavigableAgentTurn() {
		var command = CoreCommands.CreateRegistry().Require(CoreCommands.AgentJumpToTurn);

		Assert.Equal(CommandLocation.Web, command.RunsIn);
		Assert.Equal("alt+up", Assert.Single(command.DefaultKeybindings).Key);
		Assert.Equal("agentFocused && agentTurnNavigable", command.When);
	}

	[Fact]
	public void AgentJumpToLatest_UsesTheFocusedAgentBinding() {
		var command = CoreCommands.CreateRegistry().Require(CoreCommands.AgentJumpToLatest);

		Assert.Equal(CommandLocation.Web, command.RunsIn);
		Assert.Equal("alt+down", Assert.Single(command.DefaultKeybindings).Key);
		Assert.Equal("agentFocused", command.When);
	}

	[Fact]
	public void ToggleAgentCommandOutput_UsesTheFocusedAgentBinding() {
		var command = CoreCommands.CreateRegistry().Require(CoreCommands.ToggleAgentCommandOutput);

		Assert.Equal(CommandLocation.Web, command.RunsIn);
		Assert.Equal("alt+o", Assert.Single(command.DefaultKeybindings).Key);
		Assert.Equal("agentFocused && agentCommandOutputAvailable", command.When);
	}

	[Fact]
	public void ToggleAgentMermaidPreview_UsesTheFocusedAgentBinding() {
		var command = CoreCommands.CreateRegistry().Require(CoreCommands.ToggleAgentMermaidPreview);

		Assert.Equal(CommandLocation.Web, command.RunsIn);
		Assert.Equal("alt+m", Assert.Single(command.DefaultKeybindings).Key);
		Assert.Equal("agentFocused", command.When);
	}

	[Fact]
	public void OpenAgentPlan_UsesTheFocusedAgentBinding() {
		var command = CoreCommands.CreateRegistry().Require(CoreCommands.OpenAgentPlan);

		Assert.Equal(CommandLocation.Web, command.RunsIn);
		Assert.Equal("alt+p", Assert.Single(command.DefaultKeybindings).Key);
		Assert.Equal("agentFocused", command.When);
	}

	[Theory]
	[InlineData(CoreCommands.OpenFolder, "$mod+Shift+o")]
	[InlineData(CoreCommands.CloseWindow, "$mod+Shift+w")]
	[InlineData(CoreCommands.Exit, "$mod+q")]
	public void NativeShellCommands_AreGuardedAndBound(string id, string key) {
		var command = CoreCommands.CreateRegistry().Require(id);

		Assert.Equal(CommandLocation.Web, command.RunsIn);
		Assert.Equal(CommandOwner.Client, command.Owner);
		Assert.Equal("nativeShell", command.When);
		Assert.Equal(key, Assert.Single(command.DefaultKeybindings).Key);
	}

	[Theory]
	[InlineData(CoreCommands.IncreaseFontSize)]
	[InlineData(CoreCommands.DecreaseFontSize)]
	[InlineData(CoreCommands.ResetFontSize)]
	[InlineData(CoreCommands.InstallTheme)]
	[InlineData(CoreCommands.InstallThemeFromFile)]
	[InlineData(CoreCommands.SelectTheme)]
	[InlineData(CoreCommands.CycleThemeMode)]
	[InlineData(CoreCommands.UndoThemeOverride)]
	[InlineData(CoreCommands.ResetTheme)]
	[InlineData(CoreCommands.ToggleWindow)]
	[InlineData(CoreCommands.EnableAutomaticInference)]
	public void PresentationCommands_AreClientOwned(string id) {
		var command = CoreCommands.CreateRegistry().Require(id);

		Assert.Equal(CommandOwner.Client, command.Owner);
	}

	[Theory]
	[InlineData(SessionCommands.NewSession)]
	[InlineData(SessionCommands.LoadSession)]
	[InlineData(SessionCommands.UnloadSession)]
	[InlineData(SessionCommands.DeleteSession)]
	public void CatalogOwnedSessionCommands_AreHostScoped(string id) {
		var command = CoreCommands.CreateRegistry().Require(id);

		Assert.Equal(CommandScope.Host, command.Scope);
		Assert.Equal("weavie.session.lifecycle", command.ExecutionLane);
	}

	[Fact]
	public void ForkSession_IsSessionScopedButSharesTheLifecycleLane() {
		var command = CoreCommands.CreateRegistry().Require(SessionCommands.ForkSession);

		Assert.Equal(CommandScope.Session, command.Scope);
		Assert.Equal("weavie.session.lifecycle", command.ExecutionLane);
	}

	[Fact]
	public async Task TestShellCommands_ExecuteInFifoOrder() {
		var registry = CoreCommands.CreateRegistry();
		var dispatcher = new CommandDispatcher(registry);
		var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		dispatcher.RegisterHandler(
			CoreCommands.RunTests,
			(_, _) => Task.FromResult(CommandResult.Success()));
		dispatcher.RegisterHandler(CoreCommands.RunTestsInFile, (_, _) => {
			secondEntered.TrySetResult();
			return Task.FromResult(CommandResult.Success());
		});

		var first = await dispatcher.PrepareAsync(CoreCommands.RunTests, null, CancellationToken.None);
		var secondTask = dispatcher.PrepareAsync(CoreCommands.RunTestsInFile, null, CancellationToken.None);

		Assert.False(secondEntered.Task.IsCompleted);
		await first.CompleteAsync(CancellationToken.None);
		var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(2));
		await second.CompleteAsync(CancellationToken.None);
	}

	[Fact]
	public void CommandCatalog_EmitsClientOwnership() {
		var command = CoreCommands.CreateRegistry().Require(CoreCommands.IncreaseFontSize);

		using var catalog = JsonDocument.Parse(CommandCatalog.BuildCommandsArrayJson([command], []));

		Assert.Equal("client", catalog.RootElement[0].GetProperty("owner").GetString());
		Assert.Equal("weavie.font", catalog.RootElement[0].GetProperty("executionLane").GetString());
		Assert.Equal("session", catalog.RootElement[0].GetProperty("scope").GetString());
	}
}
