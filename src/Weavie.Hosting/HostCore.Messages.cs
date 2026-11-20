using System.Text.Json;
using Weavie.AcpDistribution;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Git;
using Weavie.Core.Inference;
using Weavie.Core.Layout;
using Weavie.Core.Remote;
using Weavie.Core.Search;
using Weavie.Core.Sessions;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private void WireHostMessages() {
		WireSystemNotificationMessages();
		WireCommandUsage();

		WireDeviceMessages();
		var connection = _messages.Host.Feature("connection");
		connection.HandleOwnedAfterResponse(
			"hello", MessageActivity.ActionFailed("Connecting to Weavie"), WireJson.Default.HelloRequest, WireJson.Default.HostHello,
			(request, page, _) => {
				page.Home = new PageHome(request.Home);
				bool home = request.Home is null;
				return Task.FromResult(new ResponseWithCompletion<HostHello>(
					BuildHello(home),
					_ => {
						// Setup and the tip are a device's own, so only a page this host serves is offered them. Setup
						// covers this machine's inference, so a page from here gets the inference offer once set up.
						if (home && !_settings.RequireBool(CoreSettings.GettingStartedCompleted)) {
							_messages.Host.Feature("gettingStarted").Target(page).Publish("show", WireJson.Default.EmptyPayload, EmptyPayload.Value);
						} else {
							if (home) OfferStartupTip();
							OfferAutomaticInference(home ? null : page);
						}

						return Task.CompletedTask;
					}));
			});

		var clipboard = _messages.Host.Feature("clipboard");
		clipboard.Handle("write", MessageActivity.ActionFailed("Copying to the clipboard"), WireJson.Default.ClipboardWrite, (message, _) => {
			_platform.WriteClipboard(message.Text);
			return Task.CompletedTask;
		});
		clipboard.Handle(
			"read", MessageActivity.ActionFailed("Reading the clipboard"), WireJson.Default.EmptyPayload, WireJson.Default.ClipboardText,
			(_, _) => Task.FromResult(new ClipboardText(_platform.ReadClipboard())));
		clipboard.Handle(
			"readImage", MessageActivity.ActionFailed("Reading an image from the clipboard"), WireJson.Default.EmptyPayload, WireJson.Default.ClipboardImage,
			(_, _) => {
				var image = _platform.ReadClipboardImage();
				return Task.FromResult(new ClipboardImage(
					image.Mime,
					Convert.ToBase64String(image.Bytes)));
			});

		_messages.Host.Feature("platform").Handle("openUrl", MessageActivity.ActionFailed("Opening a link"), WireJson.Default.OpenUrlMessage, (message, _) => {
			if (IsHttpUrl(message.Url)) {
				_platform.OpenExternalUrl(message.Url);
			} else {
				Log($"[bridge] refused non-http URL: {message.Url}");
			}

			return Task.CompletedTask;
		});

		_messages.Host.Feature("layout").Handle("tool", MessageActivity.ActionFailed("Updating the layout"), WireJson.Default.ToolLayoutMessage, WireJson.Default.Boolean, (message, _) => {
			_layout.ChangeTool(message.Kind, message.Action);
			return Task.FromResult(true);
		});
		_messages.Host.Feature("layout").Handle("resize", MessageActivity.ActionFailed("Resizing the layout"), WireJson.Default.LayoutResizeMessage, WireJson.Default.Boolean, (message, _) => {
			_layout.Resize(message.Expected, message.Root);
			return Task.FromResult(true);
		});

		var remoteAgents = _messages.Host.Feature("remoteAgents");
		remoteAgents.Handle("add", MessageActivity.ActionFailed("Adding a remote agent"), WireJson.Default.RemoteAgentMessage, (message, _) => {
			if (!string.IsNullOrWhiteSpace(message.Name)) {
				_remoteAgents.Add(new RemoteAgent(message.Name, message.Url, message.Token));
			}

			return Task.CompletedTask;
		});
		remoteAgents.Handle("remove", MessageActivity.ActionFailed("Removing a remote agent"), WireJson.Default.RemoteAgentName, (message, _) => {
			_remoteAgents.Remove(message.Name);
			return Task.CompletedTask;
		});

		var rail = _messages.Host.Feature("rail");
		rail.Handle("setLastLocation", MessageActivity.ActionFailed("Saving the rail location"), WireJson.Default.RailLocation, (message, _) => {
			_railState.SetLastLocation(message.Location);
			return Task.CompletedTask;
		});
		rail.Handle("setParkedCollapsed", MessageActivity.ActionFailed("Folding parked sessions"), WireJson.Default.RailParkedCollapsed, (message, _) => {
			_railState.SetParkedCollapsed(message.Collapsed);
			return Task.CompletedTask;
		});
		rail.Handle("setPanelPinned", MessageActivity.ActionFailed("Pinning the session panel"), WireJson.Default.RailPanelPinned, (message, _) => {
			_railState.SetPanelPinned(message.Pinned);
			return Task.CompletedTask;
		});
		rail.Handle("setPanelWidth", MessageActivity.ActionFailed("Resizing the session panel"), WireJson.Default.RailPanelWidth, (message, _) => {
			_railState.SetPanelWidth(message.Width);
			return Task.CompletedTask;
		});
		rail.Handle("setFullscreenWidth", MessageActivity.ActionFailed("Resizing the fullscreen pane"), WireJson.Default.RailFullscreenWidth, (message, _) => {
			_railState.SetFullscreenWidth(message.Width);
			return Task.CompletedTask;
		});
		rail.Handle("setSelected", MessageActivity.ActionFailed("Saving the rail selection"), WireJson.Default.RailSelectionMessage, (message, _) => {
			_railState.SetSelected(message.BackendId, message.Slot);
			return Task.CompletedTask;
		});

		var search = _messages.Host.Feature("search");
		search.Handle("setOptions", MessageActivity.ActionFailed("Saving search options"), WireJson.Default.SearchOptionsMessage, (message, _) => {
			_searchState.SetOptions(new GrepOptions {
				CaseSensitive = message.CaseSensitive,
				WholeWord = message.WholeWord,
				Regex = message.Regex,
				ExcludeGitignored = message.ExcludeGitignored,
				Include = message.Include,
				Exclude = message.Exclude,
			});
			return Task.CompletedTask;
		});
		search.Handle("addRecent", MessageActivity.ActionFailed("Saving a recent search"), WireJson.Default.SearchTerm, (message, _) => {
			_searchState.AddRecentTerm(message.Term);
			return Task.CompletedTask;
		});

		_messages.Host.Feature("reviewOverview").Handle(
			"setCollapsed", MessageActivity.ActionFailed("Saving the overview's collapsed sections"), WireJson.Default.ReviewOverviewCollapse, (message, _) => {
				_reviewOverviewState.SetCollapsed(message.Section, message.Collapsed);
				return Task.CompletedTask;
			});

		var acpRegistry = _messages.Host.Feature("acpRegistry");
		acpRegistry.Handle("remove", MessageActivity.ActionFailed("Removing an agent"), WireJson.Default.AcpAgentMessage, (message, _) => {
			EnsureProviderCanBeRemoved(message.Id);
			_acpAgents.Remove(message.Id);
			return Task.CompletedTask;
		});
		acpRegistry.Handle("reload", MessageActivity.ActionFailed("Reloading agents"), WireJson.Default.EmptyPayload, (_, _) => {
			var currentIds = _acpAgents.ProviderIds;
			_acpAgents.Reload(proposed => EnsureProvidersCanBeReplaced(currentIds, proposed));
			return Task.CompletedTask;
		});

		_messages.Host.Feature("git").Handle(
			"branches", MessageActivity.ActionFailed("Listing branches"), WireJson.Default.BranchesRequest, WireJson.Default.StringArray,
			ListBranchesAsync);

		_messages.Host.Feature("sessions").HandleKeyedOwned(
			"invoke", CommandActivity, WireJson.Default.CommandRequest, WireJson.Default.CommandWireResult,
			CommandExecutionLane,
			async (message, page, ct) => CommandWireResult.From(
				await InvokeHostSessionCommandAsync(message, page, ct).ConfigureAwait(false)));
		_messages.Host.Feature("sessionCreation").HandleWithCallerProgress(
			"previewBranch", MessageActivity.Droppable("Suggesting a branch name"), WireJson.Default.HostBranchPreviewRequest, WireJson.Default.BranchPreviewResult,
			(message, ct) => PreviewBranchNameFromHostAsync(message, ct));
		// Concurrent: neither may wait behind a branch query, nor hold one up.
		_messages.Host.Feature("sessionCreation").HandleConcurrent(
			"workflows", MessageActivity.Droppable("Listing workflows"), WireJson.Default.WorkflowStartTarget, WireJson.Default.WorkflowStartCatalog,
			WorkflowStartCatalogAsync);
		_messages.Host.Feature("sessionCreation").HandleConcurrentWithCallerProgress(
			"suggestWorkflow", MessageActivity.Droppable("Suggesting a workflow"), WireJson.Default.WorkflowSuggestionRequest, WireJson.Default.WorkflowSuggestionResult,
			SuggestWorkflowAsync);
		_messages.Host.Feature("sessionCreation").HandleConcurrent(
			"roleControls", MessageActivity.NoDeadline("Asking the agent for its options"), WireJson.Default.RoleControlsRequest, WireJson.Default.WorkflowRoleControls,
			RoleControlsAsync);
		_messages.Host.Feature("sessionCreation").HandleConcurrentWithCallerProgress(
			"suggestRole", MessageActivity.Droppable("Suggesting a model"), WireJson.Default.RoleSuggestionRequest, WireJson.Default.RoleSuggestionResult,
			SuggestRoleAsync);
		_messages.Host.Feature("commands").HandleKeyed(
			"invoke", CommandActivity, WireJson.Default.CommandRequest, WireJson.Default.CommandWireResult,
			CommandExecutionLane,
			async (message, ct) => CommandWireResult.From(
				await InvokeClientCommandOnHostAsync(message, ct).ConfigureAwait(false)));

		_messages.Host.Feature("applicationMenu").HandleOwned(
			"state", MessageActivity.ActionFailed("Updating the application menu"), WireJson.Default.ApplicationMenuState,
			(message, peer, _) => {
				_applicationMenuOwner = peer;
				_platform.ApplicationMenu.Apply(message);
				return Task.CompletedTask;
			});

		var window = _messages.Host.Feature("window");
		window.Handle("menu", MessageActivity.ActionFailed("Running a menu action"), WireJson.Default.JsonElement, (message, _) => {
			_menuActions.Enqueue(() => (_shellMenu ?? throw new InvalidOperationException("Host menu actions arrived before startup."))
				.HandleMenuAction(message));
			return Task.CompletedTask;
		});
	}

	private void OnApplicationMenuActivated(ApplicationMenuActivation activation) {
		if (_applicationMenuOwner is { } owner) {
			_messages.Host.Feature("applicationMenu").Target(owner).Publish("invoke", WireJson.Default.ApplicationMenuActivation, activation);
		}
	}

	private void OnApplicationMenuPeerDisconnected(MessagePeer peer) => _ui.Post(() => {
		if (ReferenceEquals(_applicationMenuOwner, peer)) {
			_applicationMenuOwner = null;
			_platform.ApplicationMenu.Clear();
		}
	});

	private async Task<CommandResult> InvokeHostSessionCommandAsync(
		CommandRequest message,
		MessagePeer page,
		CancellationToken ct) {
		var args = message.Args;
		string? id = ReadString(args, "id");
		return message.Id switch {
			CoreCommands.ResetCommandLearning => ResetCommandLearning(),
			CoreCommands.EnableAutomaticInference => InferenceCommands.EnableAutomatic(_settings),
			SessionCommands.NewSession => _messages.TryGetHome(page.Transport, out var home)
				? await NewSessionFromHostAsync(args, home, ct).ConfigureAwait(false)
				: CommandResult.Failure("This page hasn't connected yet."),
			SessionCommands.RecreateSession => await RecreateSessionAsync(
				null, id, ReadString(args, "agentProviderId"), SessionCommands.Flag(args, "stopBackgroundWork"), new CommandInvocationContext(), ct).ConfigureAwait(false),
			SessionCommands.ReloadSession => await ReloadSessionAsync(
				null, id, SessionCommands.Flag(args, "stopBackgroundWork"), new CommandInvocationContext(), ct).ConfigureAwait(false),
			SessionCommands.CancelOperation => await CancelOperationAsync(id).ConfigureAwait(false),
			SessionCommands.LoadSession => await LoadSessionAsync(id, SessionCommands.Flag(args, "clearCorruptReview"), ct).ConfigureAwait(false),
			SessionCommands.OpenHomeSession => await OpenHomeSessionAsync(ct).ConfigureAwait(false),
			SessionCommands.ForgetRepo => await ForgetRepoAsync(ReadString(args, "repo"), ct).ConfigureAwait(false),
			SessionCommands.UnloadSession => await UnloadSessionAsync(
				null,
				id,
				SessionCommands.Flag(args, "stopBackgroundWork"),
				new CommandInvocationContext(),
				ct).ConfigureAwait(false),
			SessionCommands.DeleteSession when SessionCommands.Flag(args, "classify") =>
				await ClassifyDeleteAsync(id, ct).ConfigureAwait(false),
			SessionCommands.DeleteSession => await DeleteSessionAsync(
				null,
				id,
				SessionCommands.DeleteConsent(SessionCommands.Flag(args, "force"), SessionCommands.Flag(args, "keepFolder")),
				SessionCommands.Flag(args, "stopBackgroundWork"),
				new CommandInvocationContext(),
				ct).ConfigureAwait(false),
			_ => CommandResult.Failure($"'{message.Id}' is not a host-scoped session command."),
		};
	}

	private async Task<CommandResult> InvokeClientCommandOnHostAsync(
		CommandRequest message,
		CancellationToken ct) {
		if (!_commandRegistry.TryGet(message.Id, out var definition)
			|| definition is not { RunsIn: CommandLocation.Core, Owner: CommandOwner.Client }) {
			return CommandResult.Failure($"'{message.Id}' is not a client-owned Core command.");
		}

		return await _clientCommands.InvokeAsync(
			message.Id,
			message.Args?.GetRawText(),
			ct).ConfigureAwait(false);
	}

	private MessageActivity CommandActivity(CommandRequest message) =>
		_commandRegistry.TryGet(message.Id, out var definition)
			? MessageActivity.Of($"Running “{definition.Title}”", definition.OnTimeout ?? OperationSeverity.ActionFailed)
			: MessageActivity.ActionFailed($"Running “{message.Id}”");

	private string CommandExecutionLane(CommandRequest message) =>
		_commandRegistry.TryGet(message.Id, out var definition)
			? definition.ExecutionLaneFor(message.Args?.GetRawText())
			: "unknown-command";

	private Task<CommandResult> NewSessionFromHostAsync(JsonElement? args, DeviceIdentity? device, CancellationToken ct) {
		if (args is not { ValueKind: JsonValueKind.Object } element) {
			return Task.FromResult(CommandResult.Failure("New session arguments must be an object."));
		}

		var request = element.Deserialize(WireJson.Default.NewSessionRequest)
			?? throw new JsonException("New session arguments were empty.");
		bool hasSource = element.TryGetProperty("source", out var sourceElement)
			&& sourceElement.ValueKind != JsonValueKind.Null;
		var source = ReadSessionAddress(element, "source");
		return hasSource && source is null
			? Task.FromResult(CommandResult.Failure("The source session address is invalid."))
			: NewSessionAsync(source, request, _ => device, ct);
	}

	private static SessionAddress? ReadSessionAddress(JsonElement args, string name) {
		if (!args.TryGetProperty(name, out var value)
			|| value.ValueKind != JsonValueKind.Object) {
			return null;
		}

		string? slot = ReadString(value, "slot");
		string? incarnation = ReadString(value, "incarnation");
		return string.IsNullOrWhiteSpace(slot) || string.IsNullOrWhiteSpace(incarnation)
			? null
			: new SessionAddress(slot, incarnation);
	}

	private static string? ReadString(JsonElement? args, string name) =>
		args is { ValueKind: JsonValueKind.Object } element
		&& element.TryGetProperty(name, out var value)
		&& value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;

	private async Task<BranchPreviewResult> PreviewBranchNameFromHostAsync(
		HostBranchPreviewRequest message,
		CancellationToken ct) {
		Launch launch;
		try {
			launch = await LaunchAsync(message.SourceId, message.Repo, ct).ConfigureAwait(false);
		} catch (Exception ex) when (ex is InvalidOperationException or GitException) {
			return BranchPreviewResult.Failed(ex.Message);
		}

		return await PreviewBranchNameAsync(
			launch,
			message.Prompt,
			message.Attachments,
			message.UserInitiated
				? InferenceInvocationOrigin.UserInitiated
				: InferenceInvocationOrigin.Automatic,
			ct).ConfigureAwait(false);
	}

	private HostHello BuildHello(bool home) {
		using var layout = JsonDocument.Parse(LayoutSerialization.SerializeCompact(_layout.Current));
		var hello = new HostHello(
			_device,
			_hostIncarnation,
			BuildNumber,
			BuildSessionCatalog(),
			layout.RootElement.Clone(),
			home
				? new HomeSnapshot(
					RemoteAgentSnapshots(),
					BuildRailSnapshot(),
					BuildSearchSnapshot(),
					BuildReviewOverviewSnapshot(),
					ParseJsonElement(_global.DeviceSettingsJson()))
				: null,
			RepoStates(),
			KnownRepos(),
			[.. _sources.ConnectedLinkHosts()],
			ParseJsonElement(_global.AgentDefaultsJson()),
			BuildCommandUsage(),
			new CommandCatalogSnapshot(
				ParseJsonElement(_keybindings.BuildCommandsJson()),
				ParseJsonElement(_keybindings.BuildKeybindingsJson())));

		Ready?.Invoke();
		MarkAutoConfigPageReady();
		MarkOpenPathPageReady();
		SurfacePriorCrash();
		SurfaceSessionStartupNotices();
		if (_environmentImportFailure.Length > 0) {
			Notify("warn", _environmentImportFailure, "shell-environment");
		}

		if (_settings.IsMalformed) {
			NotifySettingsMalformed(true);
		}

		NotifyUnknownKeybindingCommands(_keybindings.UnknownCommands);
		if (_keybindings.IsMalformed) {
			NotifyKeybindingsMalformed(true);
		}

		PushDrainStateToWeb();
		return hello;
	}

	private static JsonElement ParseJsonElement(string json) {
		using var document = JsonDocument.Parse(json);
		return document.RootElement.Clone();
	}

	internal sealed record ClipboardWrite(string Text);

	internal sealed record ClipboardText(string Text);

	internal sealed record ClipboardImage(string Mime, string DataB64);

	internal sealed record OpenUrlMessage(string Url);

	internal sealed record SuggestionDismissal(string Id, bool Forever);

	internal sealed record RemoteAgentMessage(string Name, string Url, string Token);

	internal sealed record RemoteAgentName(string Name);

	internal sealed record RailLocation(string Location);

	internal sealed record RailParkedCollapsed(bool Collapsed);

	internal sealed record RailSelectionMessage(string BackendId, string Slot);

	internal sealed record RailPanelPinned(bool Pinned);

	internal sealed record RailPanelWidth(int Width);

	internal sealed record RailFullscreenWidth(int Width);

	internal sealed record SearchOptionsMessage(
		bool CaseSensitive,
		bool WholeWord,
		bool Regex,
		bool ExcludeGitignored,
		string Include,
		string Exclude);

	internal sealed record SearchTerm(string Term);

	internal sealed record ReviewOverviewCollapse(string Section, bool Collapsed);

	internal sealed record ReviewOverviewSnapshot(string[] Collapsed);

	/// <summary>A page's hello: the home that serves it, or none when this host does.</summary>
	internal sealed record HelloRequest(DeviceIdentity? Home);

	internal sealed record HostHello(
		DeviceIdentity Device,
		string HostIncarnation,
		string BuildNumber,
		SessionCatalogEntry[] Sessions,
		JsonElement Layout,
		HomeSnapshot? Home,
		RepoState[] Repos,
		KnownRepo[] KnownRepos,
		string[] SourceLinkHosts,
		JsonElement AgentDefaults,
		CommandUsageSnapshot CommandUsage, CommandCatalogSnapshot CommandCatalog);

	/// <summary>What only a page this host serves reads: its remote agents, chrome state and device settings.</summary>
	internal sealed record HomeSnapshot(
		RemoteAgentSnapshot[] RemoteAgents,
		RailSnapshot Rail,
		SearchSnapshot Search,
		ReviewOverviewSnapshot ReviewOverview,
		JsonElement DeviceSettings);

	internal sealed record CommandCatalogSnapshot(JsonElement Commands, JsonElement Keybindings);

	internal sealed record RemoteAgentSnapshot(string Name, string Url, string Token);

	internal sealed record RailSnapshot(
		string LastLocation,
		RailSelection? Selected,
		bool PanelPinned,
		int PanelWidth,
		bool ParkedCollapsed,
		int FullscreenWidth);

	internal sealed record RailSelection(string BackendId, string Slot);

	internal sealed record SearchSnapshot(SearchOptionsSnapshot Options, string[] RecentTerms);

	internal sealed record SearchOptionsSnapshot(
		bool CaseSensitive,
		bool WholeWord,
		bool Regex,
		bool ExcludeGitignored,
		string Include,
		string Exclude);

	private RemoteAgentSnapshot[] RemoteAgentSnapshots() =>
		[.. _remoteAgents.Agents.Select(agent => new RemoteAgentSnapshot(agent.Name, agent.Url, agent.Token))];

	private RailSnapshot BuildRailSnapshot() => new(
		_railState.LastLocation,
		_railState.Selected is { } selected ? new RailSelection(selected.BackendId, selected.Slot) : null,
		_railState.PanelPinned,
		_railState.PanelWidth,
		_railState.ParkedCollapsed,
		_railState.FullscreenWidth);

	private ReviewOverviewSnapshot BuildReviewOverviewSnapshot() => new([.. _reviewOverviewState.Collapsed]);

	private SearchSnapshot BuildSearchSnapshot() {
		var search = _searchState.Current;
		return new(
			new SearchOptionsSnapshot(
				search.Options.CaseSensitive,
				search.Options.WholeWord,
				search.Options.Regex,
				search.Options.ExcludeGitignored,
				search.Options.Include,
				search.Options.Exclude),
			[.. search.RecentTerms]);
	}
}

internal sealed record AcpInstallMessage(string Id, string Distribution, string Operation);

internal sealed record AcpAgentMessage(string Id);
