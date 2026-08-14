using System.Text.Json;
using Weavie.Core.Commands;
using Weavie.Core.Git;
using Weavie.Core.Layout;
using Weavie.Core.Remote;
using Weavie.Core.Search;
using Weavie.Core.Sessions;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private void WireHostMessages() {
		WireSystemNotificationMessages();

		var connection = _messages.Host.Feature("connection");
		connection.HandleAfterResponse<HelloRequest, HostHello>(
			"hello",
			(_, _) => Task.FromResult(new ResponseWithCompletion<HostHello>(
				BuildHello(),
				_ => {
					OfferAutomaticInference();
					return Task.CompletedTask;
				})));

		var clipboard = _messages.Host.Feature("clipboard");
		clipboard.Handle<ClipboardWrite>("write", (message, _) => {
			_platform.WriteClipboard(message.Text);
			return Task.CompletedTask;
		});
		clipboard.Handle<EmptyMessage, ClipboardText>(
			"read",
			(_, _) => Task.FromResult(new ClipboardText(_platform.ReadClipboard())));
		clipboard.Handle<EmptyMessage, ClipboardImage>(
			"readImage",
			(_, _) => {
				var image = _platform.ReadClipboardImage();
				return Task.FromResult(new ClipboardImage(
					image.Mime,
					Convert.ToBase64String(image.Bytes)));
			});

		_messages.Host.Feature("platform").Handle<OpenUrlMessage>("openUrl", (message, _) => {
			if (IsHttpUrl(message.Url)) {
				_platform.OpenExternalUrl(message.Url);
			} else {
				Log($"[bridge] refused non-http URL: {message.Url}");
			}

			return Task.CompletedTask;
		});

		_messages.Host.Feature("diagnostics").Handle<WebLogMessage>("log", (message, _) => {
			Log($"[web:{message.Level}] {message.Message}");
			return Task.CompletedTask;
		});

		_messages.Host.Feature("layout").Handle<JsonElement>("changed", (message, _) => {
			HandleLayoutChanged(message);
			return Task.CompletedTask;
		});

		_messages.Host.Feature("suggestions").Handle<SuggestionDismissal>("dismiss", (message, _) => {
			DismissSuggestion(message.Id, message.Forever);
			return Task.CompletedTask;
		});

		var remoteAgents = _messages.Host.Feature("remoteAgents");
		remoteAgents.Handle<RemoteAgentMessage>("add", (message, _) => {
			if (!string.IsNullOrWhiteSpace(message.Name)) {
				_remoteAgents.Add(new RemoteAgent(message.Name, message.Url, message.Token));
			}

			return Task.CompletedTask;
		});
		remoteAgents.Handle<RemoteAgentName>("remove", (message, _) => {
			_remoteAgents.Remove(message.Name);
			return Task.CompletedTask;
		});

		var rail = _messages.Host.Feature("rail");
		rail.Handle<RailLocation>("setLastLocation", (message, _) => {
			_railState.SetLastLocation(message.Location);
			return Task.CompletedTask;
		});
		rail.Handle<RailPromotionSet>("setPromoted", (message, _) => {
			_railState.SetPromoted(message.Promoted);
			return Task.CompletedTask;
		});
		rail.Handle<RailSelectionMessage>("setSelected", (message, _) => {
			_railState.SetSelected(message.BackendId, message.Slot);
			return Task.CompletedTask;
		});

		var search = _messages.Host.Feature("search");
		search.Handle<SearchOptionsMessage>("setOptions", (message, _) => {
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
		search.Handle<SearchTerm>("addRecent", (message, _) => {
			_searchState.AddRecentTerm(message.Term);
			return Task.CompletedTask;
		});

		_messages.Host.Feature("agentDefaults").Handle<AgentProviderMessage>(
			"setProvider",
			(message, _) => {
				RememberDefaultProvider(message.ProviderId);
				return Task.CompletedTask;
			});

		_messages.Host.Feature("git").Handle<EmptyMessage, string[]>(
			"branches",
			(_, ct) => ListBranchesAsync(ct));

		_messages.Host.Feature("sessions").HandleKeyed<CommandRequest, CommandWireResult>(
			"invoke",
			CommandExecutionLane,
			async (message, ct) => ToWireResult(
				await InvokeHostSessionCommandAsync(message, ct).ConfigureAwait(false)));
		_messages.Host.Feature("sessionCreation").Handle<HostBranchPreviewRequest, BranchPreviewResult>(
			"previewBranch",
			(message, ct) => PreviewBranchNameFromHostAsync(message, ct));
		_messages.Host.Feature("commands").HandleKeyed<CommandRequest, CommandWireResult>(
			"invoke",
			CommandExecutionLane,
			async (message, ct) => ToWireResult(
				await InvokeClientCommandOnHostAsync(message, ct).ConfigureAwait(false)));

		var window = _messages.Host.Feature("window");
		window.Handle<JsonElement>("control", (message, _) => {
			_shell?.HandleWindowControl(message);
			return Task.CompletedTask;
		});
		window.Handle<JsonElement>("resize", (message, _) => {
			_shell?.HandleWindowResize(message);
			return Task.CompletedTask;
		});
		window.HandleAfterEvent<JsonElement>("menu", (message, _) =>
			Task.FromResult<Func<CancellationToken, Task>>(ct => _ui.InvokeAsync(() => {
				(_shellMenu ?? throw new InvalidOperationException("Host menu actions arrived before startup."))
					.HandleMenuAction(message);
				return Task.CompletedTask;
			}, ct)));
	}

	private async Task<CommandResult> InvokeHostSessionCommandAsync(
		CommandRequest message,
		CancellationToken ct) {
		var args = message.Args;
		string? id = ReadString(args, "id");
		return message.Id switch {
			SessionCommands.NewSession => await NewSessionFromHostAsync(args, ct).ConfigureAwait(false),
			SessionCommands.LoadSession => await LoadSessionAsync(id, ct).ConfigureAwait(false),
			SessionCommands.UnloadSession => await UnloadSessionAsync(
				null,
				id,
				new CommandInvocationContext(),
				ct).ConfigureAwait(false),
			SessionCommands.DeleteSession when args is { } deleteArgs
				&& ReadBool(deleteArgs, "classify") =>
					await ClassifyDeleteAsync(id, ct).ConfigureAwait(false),
			SessionCommands.DeleteSession => await DeleteSessionAsync(
				null,
				id,
				ReadBool(args, "force"),
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

	private string CommandExecutionLane(CommandRequest message) =>
		_commandRegistry.TryGet(message.Id, out var definition)
			? definition.ExecutionLane
			: "unknown-command";

	private Task<CommandResult> NewSessionFromHostAsync(JsonElement? args, CancellationToken ct) {
		if (args is not { ValueKind: JsonValueKind.Object } element) {
			return Task.FromResult(CommandResult.Failure("New session arguments must be an object."));
		}

		var request = JsonSerializer.Deserialize<NewSessionRequest>(
			element.GetRawText(),
			new JsonSerializerOptions(JsonSerializerDefaults.Web))
			?? throw new JsonException("New session arguments were empty.");
		bool hasSource = element.TryGetProperty("source", out var sourceElement)
			&& sourceElement.ValueKind != JsonValueKind.Null;
		var source = ReadSessionAddress(element, "source");
		return hasSource && source is null
			? Task.FromResult(CommandResult.Failure("The source session address is invalid."))
			: NewSessionAsync(source, request, ct);
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

	private static bool ReadBool(JsonElement? args, string name) =>
		args is { ValueKind: JsonValueKind.Object } element
		&& element.TryGetProperty(name, out var value)
		&& value.ValueKind == JsonValueKind.True;

	private SessionSlot? SourceSlot(string? sourceId) =>
		string.IsNullOrWhiteSpace(sourceId) ? null : _sessions?.Find(sourceId);

	private Task<BranchPreviewResult> PreviewBranchNameFromHostAsync(
		HostBranchPreviewRequest message,
		CancellationToken ct) {
		var source = SourceSlot(message.SourceId);
		if (!string.IsNullOrWhiteSpace(message.SourceId) && source is null) {
			return Task.FromResult(new BranchPreviewResult(string.Empty, "The source session no longer exists."));
		}

		return PreviewBranchNameAsync(
			source?.WorktreePath ?? WorkspaceRoot,
			message.Prompt,
			message.AgentProviderId,
			ct);
	}

	private HostHello BuildHello() {
		using var layout = JsonDocument.Parse(LayoutSerialization.SerializeCompact(_layout.Current));
		var search = _searchState.Current;
		var hello = new HostHello(
			_hostIncarnation,
			BuildNumber,
			BuildSessionCatalog(),
			layout.RootElement.Clone(),
			[.. _remoteAgents.Agents
				.Select(agent => new RemoteAgentSnapshot(agent.Name, agent.Url, agent.Token))
			],
			new RailSnapshot(
				_railState.LastLocation,
				[.. _railState.Promoted],
				RailSelectionSnapshot()),
			new SearchSnapshot(
				new SearchOptionsSnapshot(
					search.Options.CaseSensitive,
					search.Options.WholeWord,
					search.Options.Regex,
					search.Options.ExcludeGitignored,
					search.Options.Include,
					search.Options.Exclude),
				[.. search.RecentTerms]),
			ResolvedTestProfile(),
			new CommandCatalogSnapshot(
				ParseJsonElement(_keybindings.BuildCommandsJson()),
				ParseJsonElement(_keybindings.BuildKeybindingsJson())));

		Ready?.Invoke();
		MarkAutoConfigPageReady();
		SurfacePriorCrash();
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

	private sealed record EmptyMessage;

	private sealed record HelloRequest;

	private sealed record ClipboardWrite(string Text);

	private sealed record ClipboardText(string Text);

	private sealed record ClipboardImage(string Mime, string DataB64);

	private sealed record OpenUrlMessage(string Url);

	private sealed record WebLogMessage(string Level, string Message);

	private sealed record SuggestionDismissal(string Id, bool Forever);

	private sealed record RemoteAgentMessage(string Name, string Url, string Token);

	private sealed record RemoteAgentName(string Name);

	private sealed record RailLocation(string Location);

	private sealed record RailPromotionSet(string[] Promoted);

	private sealed record RailSelectionMessage(string BackendId, string Slot);

	private sealed record SearchOptionsMessage(
		bool CaseSensitive,
		bool WholeWord,
		bool Regex,
		bool ExcludeGitignored,
		string Include,
		string Exclude);

	private sealed record SearchTerm(string Term);

	private sealed record AgentProviderMessage(string ProviderId);

	private sealed record HostHello(
		string HostIncarnation,
		string BuildNumber,
		SessionCatalogEntry[] Sessions,
		JsonElement Layout,
		RemoteAgentSnapshot[] RemoteAgents,
		RailSnapshot Rail,
		SearchSnapshot Search,
		string TestProfile,
		CommandCatalogSnapshot CommandCatalog);

	private sealed record CommandCatalogSnapshot(JsonElement Commands, JsonElement Keybindings);

	private sealed record RemoteAgentSnapshot(string Name, string Url, string Token);

	private sealed record RailSnapshot(
		string LastLocation,
		string[] Promoted,
		RailSelection? Selected);

	private sealed record RailSelection(string BackendId, string Slot);

	private sealed record SearchSnapshot(SearchOptionsSnapshot Options, string[] RecentTerms);

	private sealed record SearchOptionsSnapshot(
		bool CaseSensitive,
		bool WholeWord,
		bool Regex,
		bool ExcludeGitignored,
		string Include,
		string Exclude);

	private RailSelection? RailSelectionSnapshot() =>
		_railState.Selected is { } selected
			? new RailSelection(selected.BackendId, selected.Slot)
			: null;
}
