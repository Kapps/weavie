using Weavie.Core.Commands;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="SessionCommands"/>: the declarations land in the registry with the right run
/// location, and the Core handlers parse arguments and route to the <see cref="ISessionHost"/>.
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
		Assert.True(registry.TryGet(SessionCommands.CloseSession, out var closeDef));
		Assert.Equal(CommandLocation.Core, closeDef!.RunsIn);
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
		Assert.Equal("$mod+Shift+1", select.DefaultKeybindings[0].Key);
		Assert.Equal("{\"index\":1}", select.DefaultKeybindings[0].ArgsJson);
		Assert.Equal("$mod+Shift+9", select.DefaultKeybindings[8].Key);
		Assert.Equal("{\"index\":9}", select.DefaultKeybindings[8].ArgsJson);
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
	public async Task Fork_And_Close_InvokeHost() {
		var (dispatcher, host) = NewWired();

		await dispatcher.InvokeAsync(SessionCommands.ForkSession, "{\"handoff\":\"context here\"}", CancellationToken.None);
		await dispatcher.InvokeAsync(SessionCommands.CloseSession, "{\"id\":\"abcd\"}", CancellationToken.None);

		Assert.Equal("context here", host.LastFork?.Handoff);
		Assert.True(host.CloseCalled);
		Assert.Equal("abcd", host.LastClosedId);
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

		public string? LastClosedId { get; private set; }

		public bool CloseCalled { get; private set; }

		public Task<CommandResult> NewSessionAsync(NewSessionRequest request, CancellationToken ct = default) {
			LastNew = request;
			return Task.FromResult(CommandResult.Success("created"));
		}

		public Task<CommandResult> ForkSessionAsync(ForkSessionRequest request, CancellationToken ct = default) {
			LastFork = request;
			return Task.FromResult(CommandResult.Success("forked"));
		}

		public Task<CommandResult> CloseSessionAsync(string? sessionId, CancellationToken ct = default) {
			CloseCalled = true;
			LastClosedId = sessionId;
			return Task.FromResult(CommandResult.Success("closed"));
		}
	}
}
