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
	public void TogglePlanMode_UsesNativeShiftTabBinding() {
		var command = CoreCommands.CreateRegistry().Require(CoreCommands.TogglePlanMode);

		Assert.Equal(CommandLocation.Web, command.RunsIn);
		Assert.Equal("shift+tab", Assert.Single(command.DefaultKeybindings).Key);
		Assert.Equal("agentFocused && !agentSlashMenuOpen && !agentControlPickerOpen", command.When);
	}
}
