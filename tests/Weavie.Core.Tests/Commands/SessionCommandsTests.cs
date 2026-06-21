using Weavie.Core.Commands;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// <see cref="SessionCommands"/>: declarations register with the right run location, and Core handlers
/// parse arguments and route to the <see cref="ISessionHost"/>.
/// </summary>
public sealed class SessionCommandsTests {
	[Fact]
	public void Register_AddsSessionCommands_WithExpectedRunLocations() {
		var registry = new CommandRegistry();
		SessionCommands.Register(registry);

		Assert.True(registry.TryGet(SessionCommands.NewSession, out var newDef));
		Assert.Equal(CommandLocation.Core, newDef!.RunsIn);
		Assert.True(registry.TryGet(SessionCommands.ForkSession, out var forkDef));
		Assert.Equal(CommandLocation.Core, forkDef!.RunsIn);
		Assert.True(registry.TryGet(SessionCommands.LoadSession, out var loadDef));
		Assert.Equal(CommandLocation.Core, loadDef!.RunsIn);
		Assert.True(registry.TryGet(SessionCommands.UnloadSession, out var unloadDef));
		Assert.Equal(CommandLocation.Core, unloadDef!.RunsIn);
		Assert.True(registry.TryGet(SessionCommands.DeleteSession, out var deleteDef));
		Assert.Equal(CommandLocation.Core, deleteDef!.RunsIn);
		// The delete confirm runs in the web (shows the dialog); raw delete is core/MCP.
		Assert.True(registry.TryGet(SessionCommands.DeleteSessionPrompt, out var deletePromptDef));
		Assert.Equal(CommandLocation.Web, deletePromptDef!.RunsIn);
		Assert.True(registry.TryGet(SessionCommands.NextSession, out var nextDef));
		Assert.Equal(CommandLocation.Web, nextDef!.RunsIn);
		Assert.True(registry.TryGet(SessionCommands.PrevSession, out _));
		Assert.True(registry.TryGet(SessionCommands.SwitchSession, out _));
	}

	[Fact]
	public void Register_SelectSessionByIndex_HasNineShiftBindings() {
		var registry = new CommandRegistry();
		SessionCommands.Register(registry);

		Assert.True(registry.TryGet(SessionCommands.SelectSessionByIndex, out var select));
		Assert.Equal(CommandLocation.Web, select!.RunsIn);
		Assert.False(select.ShowInPalette);
		Assert.Equal(9, select.DefaultKeybindings.Count);
		Assert.Equal("ctrl+Shift+1", select.DefaultKeybindings[0].Key);
		Assert.Equal("{\"index\":1}", select.DefaultKeybindings[0].ArgsJson);
		Assert.Equal("ctrl+Shift+9", select.DefaultKeybindings[8].Key);
		Assert.Equal("{\"index\":9}", select.DefaultKeybindings[8].ArgsJson);
	}

	[Fact]
	public void Register_NextPrevSession_BindTab_GatedTerminalFocused() {
		var registry = new CommandRegistry();
		SessionCommands.Register(registry);

		Assert.True(registry.TryGet(SessionCommands.NextSession, out var next));
		Assert.True(registry.TryGet(SessionCommands.PrevSession, out var prev));

		// ctrl+Tab / ctrl+Shift+Tab are the editor's tab chords under editorFocused; here they cycle sessions
		// whenever the editor isn't focused (!editorFocused — the exact complement, so the two never collide;
		// it also fires on load, before any pane takes focus). Literal ctrl (not $mod) keeps them off macOS's
		// Cmd+Tab. The guard sits on the binding (not the command) so a command-level When can't hide
		// Next/Previous Session from the palette while the editor or omnibar holds focus.
		Assert.Null(next!.When);
		Assert.Null(prev!.When);

		var nextBinding = Assert.Single(next.DefaultKeybindings);
		Assert.Equal("ctrl+Tab", nextBinding.Key);
		Assert.Equal("!editorFocused", nextBinding.When);

		var prevBinding = Assert.Single(prev.DefaultKeybindings);
		Assert.Equal("ctrl+Shift+Tab", prevBinding.Key);
		Assert.Equal("!editorFocused", prevBinding.When);
	}

	[Fact]
	public async Task NewSession_ParsesArgs_AndInvokesHost() {
		var (dispatcher, host) = NewWired();

		var result = await dispatcher.InvokeAsync(
			SessionCommands.NewSession,
			"{\"branch\":\"feature\",\"base\":\"main\",\"prompt\":\"do it\"}",
			CancellationToken.None);

		Assert.True(result.Ok);
		Assert.Equal("feature", host.LastNew?.Branch);
		Assert.Equal("main", host.LastNew?.Base);
		Assert.Equal("do it", host.LastNew?.Prompt);
	}

	[Fact]
	public async Task NewSession_NoArgs_PassesNulls() {
		var (dispatcher, host) = NewWired();

		await dispatcher.InvokeAsync(SessionCommands.NewSession, null, CancellationToken.None);

		Assert.NotNull(host.LastNew);
		Assert.Null(host.LastNew!.Branch);
		Assert.Null(host.LastNew.Base);
		Assert.Null(host.LastNew.Prompt);
	}

	[Fact]
	public async Task Fork_And_Unload_InvokeHost() {
		var (dispatcher, host) = NewWired();

		await dispatcher.InvokeAsync(SessionCommands.ForkSession, "{\"handoff\":\"context here\"}", CancellationToken.None);
		await dispatcher.InvokeAsync(SessionCommands.UnloadSession, "{\"id\":\"abcd\"}", CancellationToken.None);

		Assert.Equal("context here", host.LastFork?.Handoff);
		Assert.True(host.UnloadCalled);
		Assert.Equal("abcd", host.LastUnloadedId);
	}

	[Fact]
	public async Task Load_ParsesId_AndInvokesHost() {
		var (dispatcher, host) = NewWired();

		await dispatcher.InvokeAsync(SessionCommands.LoadSession, "{\"id\":\"wxyz\"}", CancellationToken.None);

		Assert.True(host.LoadCalled);
		Assert.Equal("wxyz", host.LastLoadedId);
	}

	[Fact]
	public async Task Delete_ParsesId_AndForce() {
		var (dispatcher, host) = NewWired();

		await dispatcher.InvokeAsync(SessionCommands.DeleteSession, "{\"id\":\"abcd\",\"force\":true}", CancellationToken.None);

		Assert.True(host.DeleteCalled);
		Assert.Equal("abcd", host.LastDeletedId);
		Assert.True(host.LastDeleteForce);
	}

	[Fact]
	public async Task Delete_NoForce_DefaultsFalse_AndCoercesStringForce() {
		var (dispatcher, host) = NewWired();

		await dispatcher.InvokeAsync(SessionCommands.DeleteSession, "{\"id\":\"a\"}", CancellationToken.None);
		Assert.False(host.LastDeleteForce);

		// Embedded Claude sends scalars as JSON strings; "true" must coerce to a boolean.
		await dispatcher.InvokeAsync(SessionCommands.DeleteSession, "{\"id\":\"a\",\"force\":\"true\"}", CancellationToken.None);
		Assert.True(host.LastDeleteForce);
	}

	private static (CommandDispatcher Dispatcher, FakeSessionHost Host) NewWired() {
		var registry = new CommandRegistry();
		SessionCommands.Register(registry);
		var dispatcher = new CommandDispatcher(registry);
		var host = new FakeSessionHost();
		SessionCommands.RegisterHandlers(dispatcher, host);
		return (dispatcher, host);
	}

	private sealed class FakeSessionHost : ISessionHost {
		public NewSessionRequest? LastNew { get; private set; }

		public ForkSessionRequest? LastFork { get; private set; }

		public string? LastLoadedId { get; private set; }

		public bool LoadCalled { get; private set; }

		public string? LastUnloadedId { get; private set; }

		public bool UnloadCalled { get; private set; }

		public string? LastDeletedId { get; private set; }

		public bool LastDeleteForce { get; private set; }

		public bool DeleteCalled { get; private set; }

		public Task<CommandResult> NewSessionAsync(NewSessionRequest request, CancellationToken ct = default) {
			LastNew = request;
			return Task.FromResult(CommandResult.Success("created"));
		}

		public Task<CommandResult> ForkSessionAsync(ForkSessionRequest request, CancellationToken ct = default) {
			LastFork = request;
			return Task.FromResult(CommandResult.Success("forked"));
		}

		public Task<CommandResult> LoadSessionAsync(string? sessionId, CancellationToken ct = default) {
			LoadCalled = true;
			LastLoadedId = sessionId;
			return Task.FromResult(CommandResult.Success("loaded"));
		}

		public Task<CommandResult> UnloadSessionAsync(string? sessionId, CancellationToken ct = default) {
			UnloadCalled = true;
			LastUnloadedId = sessionId;
			return Task.FromResult(CommandResult.Success("unloaded"));
		}

		public Task<CommandResult> DeleteSessionAsync(string? sessionId, bool force, CancellationToken ct = default) {
			DeleteCalled = true;
			LastDeletedId = sessionId;
			LastDeleteForce = force;
			return Task.FromResult(CommandResult.Success("deleted"));
		}
	}
}
