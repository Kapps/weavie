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
		Assert.True(registry.TryGet(SessionCommands.SubmitNewSession, out var submitDef));
		Assert.Equal(CommandLocation.Web, submitDef!.RunsIn);
		Assert.Equal(CommandOwner.Client, submitDef.Owner);
		Assert.False(submitDef.ShowInPalette);
		var submitBinding = Assert.Single(submitDef.DefaultKeybindings);
		Assert.Equal("Shift+Enter", submitBinding.Key);
		Assert.Equal("newSessionPromptFocused", submitBinding.When);
		Assert.True(submitDef.KeybindingsActiveInModal);
		Assert.True(registry.TryGet(SessionCommands.PasteNewSession, out var pasteDef));
		Assert.Equal(CommandLocation.Web, pasteDef!.RunsIn);
		Assert.Equal(CommandOwner.Client, pasteDef.Owner);
		Assert.False(pasteDef.ShowInPalette);
		var pasteBinding = Assert.Single(pasteDef.DefaultKeybindings);
		Assert.Equal("$mod+v", pasteBinding.Key);
		Assert.Equal("newSessionPromptFocused && !browserShell", pasteBinding.When);
		Assert.True(pasteDef.KeybindingsActiveInModal);
		Assert.Equal(pasteDef.ExecutionLane, submitDef.ExecutionLane);
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
		// Disconnecting a remote agent is web-handled (the agent registry is client-side); no Core handler.
		Assert.True(registry.TryGet(SessionCommands.DisconnectRemote, out var disconnectDef));
		Assert.Equal(CommandLocation.Web, disconnectDef!.RunsIn);
		Assert.False(disconnectDef.ShowInPalette);
		// Removing a promoted remote session from the rail is web-handled (the working set is client-side).
		Assert.True(registry.TryGet(SessionCommands.RemoveFromRail, out var removeDef));
		Assert.Equal(CommandLocation.Web, removeDef!.RunsIn);
		Assert.False(removeDef.ShowInPalette);
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
	public void Register_NextPrevSession_AdvertiseAvailabilityWithoutGuardingFallbackBindings() {
		var registry = new CommandRegistry();
		SessionCommands.Register(registry);

		Assert.True(registry.TryGet(SessionCommands.NextSession, out var next));
		Assert.True(registry.TryGet(SessionCommands.PrevSession, out var prev));

		// The command guard advertises whether the rail can step without changing the binding's focus behavior:
		// ctrl+Tab can still fall through from a lone editor tab to a different session. Literal ctrl (not $mod)
		// keeps the chord off macOS's Cmd+Tab.
		Assert.Equal("sessionStepAvailable", next!.When);
		Assert.Equal("sessionStepAvailable", prev!.When);

		var nextBinding = Assert.Single(next.DefaultKeybindings);
		Assert.Equal("ctrl+Tab", nextBinding.Key);
		Assert.Equal(string.Empty, nextBinding.When);

		var prevBinding = Assert.Single(prev.DefaultKeybindings);
		Assert.Equal("ctrl+Shift+Tab", prevBinding.Key);
		Assert.Equal(string.Empty, prevBinding.When);
	}

	[Fact]
	public async Task NewSession_ParsesArgs_AndInvokesHost() {
		var (dispatcher, host) = NewWired();

		var result = await dispatcher.InvokeAsync(
			SessionCommands.NewSession,
			"{\"branch\":\"feature\",\"base\":\"main\",\"prompt\":\"do it\",\"attachments\":[{\"id\":\"image-1\",\"mime\":\"image/png\",\"dataB64\":\"iVBORw==\"}],\"agentProviderId\":\"acp\"}",
			CancellationToken.None);

		Assert.True(result.Ok);
		Assert.Equal("feature", host.LastNew?.Branch);
		Assert.Equal("main", host.LastNew?.Base);
		Assert.Equal("do it", host.LastNew?.Prompt);
		Assert.Equal("acp", host.LastNew?.AgentProviderId);
		var attachment = Assert.Single(host.LastNew!.Attachments);
		Assert.Equal("image-1", attachment.Id);
		Assert.Equal("image/png", attachment.Mime);
		Assert.Equal("iVBORw==", attachment.DataB64);
	}

	[Fact]
	public async Task NewSession_NoArgs_PassesNulls() {
		var (dispatcher, host) = NewWired();

		await dispatcher.InvokeAsync(SessionCommands.NewSession, null, CancellationToken.None);

		Assert.NotNull(host.LastNew);
		Assert.Null(host.LastNew!.Branch);
		Assert.Null(host.LastNew.Base);
		Assert.Null(host.LastNew.Prompt);
		Assert.Empty(host.LastNew.Attachments);
	}

	[Theory]
	[InlineData("{\"attachments\":null}")]
	[InlineData("{\"attachments\":{}}")]
	[InlineData("{\"attachments\":[null]}")]
	[InlineData("{\"attachments\":[{\"id\":\"image-1\",\"mime\":\"image/png\"}]}")]
	[InlineData("{\"attachments\":[{\"id\":1,\"mime\":\"image/png\",\"dataB64\":\"AA==\"}]}")]
	public async Task NewSession_MalformedAttachment_FailsWithoutInvokingHost(string argsJson) {
		var (dispatcher, host) = NewWired();

		var result = await dispatcher.InvokeAsync(SessionCommands.NewSession, argsJson, CancellationToken.None);

		Assert.False(result.Ok);
		Assert.Contains("attachment", result.Error, StringComparison.OrdinalIgnoreCase);
		Assert.Null(host.LastNew);
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
	public async Task PreparedCommand_DefersEndpointDestructionUntilAfterItsReply() {
		var (dispatcher, host) = NewWired();

		var execution = await dispatcher.PrepareAsync(
			SessionCommands.UnloadSession,
			"{\"id\":\"abcd\"}",
			CancellationToken.None);

		Assert.True(host.UnloadCalled);
		Assert.False(host.AfterReplyRan);
		await execution.CompleteAsync(CancellationToken.None);
		Assert.True(host.AfterReplyRan);
	}

	[Fact]
	public async Task Load_ParsesId_AndInvokesHost() {
		var (dispatcher, host) = NewWired();

		await dispatcher.InvokeAsync(SessionCommands.LoadSession, "{\"id\":\"wxyz\"}", CancellationToken.None);

		Assert.True(host.LoadCalled);
		Assert.Equal("wxyz", host.LastLoadedId);
	}

	[Fact]
	public async Task Delete_ConfirmParsesRevisionAndIndependentConsent() {
		var (dispatcher, host) = NewWired();

		await dispatcher.InvokeAsync(
			SessionCommands.DeleteSession,
			"{\"operation\":\"confirm\",\"id\":\"abcd\",\"revision\":\"r1\",\"forceWorktree\":true,\"discardDrafts\":false}",
			CancellationToken.None);

		Assert.True(host.ConfirmCalled);
		Assert.Equal("abcd", host.LastDeletedId);
		Assert.Equal(new DeleteSessionConfirmation {
			Revision = "r1",
			ForceWorktree = true,
			DiscardDrafts = false,
		}, host.LastConfirmation);
	}

	[Fact]
	public async Task Delete_ConfirmRequiresEveryConsentField() {
		var (dispatcher, host) = NewWired();

		var result = await dispatcher.InvokeAsync(
			SessionCommands.DeleteSession,
			"{\"operation\":\"confirm\",\"id\":\"a\",\"revision\":\"r1\",\"forceWorktree\":false}",
			CancellationToken.None);

		Assert.False(result.Ok);
		Assert.Contains("discardDrafts", result.Error);
		Assert.False(host.ConfirmCalled);
	}

	[Fact]
	public async Task Delete_ConfirmRejectsStringConsentLookalikes() {
		var (dispatcher, host) = NewWired();

		var result = await dispatcher.InvokeAsync(
			SessionCommands.DeleteSession,
			"{\"operation\":\"confirm\",\"revision\":\"r1\",\"forceWorktree\":\"true\",\"discardDrafts\":false}",
			CancellationToken.None);

		Assert.False(result.Ok);
		Assert.Contains("forceWorktree", result.Error);
		Assert.False(host.ConfirmCalled);
	}

	[Theory]
	[InlineData("{\"operation\":\"preview\",\"id\":\" \"}", "id")]
	[InlineData("{\"operation\":\"confirm\",\"revision\":\"\",\"forceWorktree\":false,\"discardDrafts\":false}", "revision")]
	public async Task Delete_RejectsBlankTargetAndRevision(string args, string field) {
		var (dispatcher, host) = NewWired();

		var result = await dispatcher.InvokeAsync(SessionCommands.DeleteSession, args, CancellationToken.None);

		Assert.False(result.Ok);
		Assert.Contains(field, result.Error);
		Assert.False(host.PreviewCalled);
		Assert.False(host.ConfirmCalled);
	}

	[Fact]
	public async Task Delete_PreviewRoutesToPreviewAndReturnsPayload() {
		var (dispatcher, host) = NewWired();

		var result = await dispatcher.InvokeAsync(
			SessionCommands.DeleteSession,
			"{\"operation\":\"preview\",\"id\":\"abcd\"}",
			CancellationToken.None);

		Assert.True(host.PreviewCalled);
		Assert.Equal("abcd", host.LastPreviewedId);
		Assert.False(host.ConfirmCalled);
		Assert.True(result.Ok);
		Assert.Equal("{\"revision\":\"r1\"}", result.DataJson);
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

		public bool AfterReplyRan { get; private set; }

		public string? LastDeletedId { get; private set; }

		public DeleteSessionConfirmation? LastConfirmation { get; private set; }

		public bool ConfirmCalled { get; private set; }

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

		public Task<CommandResult> UnloadSessionAsync(
			string? sessionId,
			CommandInvocationContext context,
			CancellationToken ct = default) {
			UnloadCalled = true;
			LastUnloadedId = sessionId;
			context.AfterReply(_ => {
				AfterReplyRan = true;
				return Task.CompletedTask;
			});
			return Task.FromResult(CommandResult.Success("unloaded"));
		}

		public Task<CommandResult> ConfirmDeleteSessionAsync(
			string? sessionId,
			DeleteSessionConfirmation confirmation,
			CommandInvocationContext context,
			CancellationToken ct = default) {
			ConfirmCalled = true;
			LastDeletedId = sessionId;
			LastConfirmation = confirmation;
			return Task.FromResult(CommandResult.Success("deleted"));
		}

		public bool PreviewCalled { get; private set; }

		public string? LastPreviewedId { get; private set; }

		public Task<CommandResult> PreviewDeleteSessionAsync(string? sessionId, CancellationToken ct = default) {
			PreviewCalled = true;
			LastPreviewedId = sessionId;
			return Task.FromResult(CommandResult.Success(null, "{\"revision\":\"r1\"}"));
		}
	}
}
