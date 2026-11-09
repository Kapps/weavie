using System.Collections.Concurrent;
using System.Text.Json;
using Weavie.Core.Commands;
using Weavie.Core.Credentials;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

// Connect Service… / Disconnect Service… for every declared credential: open the vendor's key page, ask the page for the
// pasted key, validate and save it, then run whatever the connect was for (e.g. open the Notion link that needed it).
public sealed partial class HostCore {
	private readonly ConcurrentDictionary<SessionAddress, PendingConnect> _pendingConnects = new();

	private sealed record PendingConnect(string Id, Action<HostSession> Then);

	private void RegisterCredentialHandlers(HostSession session) {
		session.Commands.RegisterHandler(CoreCommands.ConnectService, (argsJson, _) =>
			Task.FromResult(Guard(() => ConnectService(session, ServiceArg(argsJson)))));
		session.Commands.RegisterHandler(CoreCommands.DisconnectService, (argsJson, _) =>
			Task.FromResult(Guard(() => DisconnectService(session, ServiceArg(argsJson)))));
		var credentials = session.Bus.Feature("credentials");
		credentials.Handle(
			"save", MessageActivity.ActionFailed("Saving a credential"), WireJson.Default.SaveCredentialMessage, WireJson.Default.CredentialSaveResult,
			(message, ct) => SaveCredentialAsync(session, message.Id, message.Key, ct));
		credentials.Handle("dismiss", MessageActivity.ActionFailed("Dismissing a credential prompt"), WireJson.Default.EmptyPayload, (_, _) => {
			session.State.Remove("credentials", "prompt");
			_pendingConnects.TryRemove(session.Address, out _);
			return Task.CompletedTask;
		});
		WirePicker(session);
	}

	private CommandResult ConnectService(HostSession session, string? id) {
		if (id is not null) {
			PromptConnect(session, id, static _ => { });
			return CommandResult.Success($"Opening your browser to connect {_credentials.Require(id).DisplayName}…");
		}

		var options = _credentials.Definitions
			.Select(definition => (Definition: definition, Connected: _credentials.IsConnected(definition.Id)))
			.OrderBy(entry => !entry.Connected)
			.Select(entry => ServiceOption(entry.Definition, entry.Connected ? "Connected" : "Not connected"));
		ShowPicker(session, new ChoicePicker("Connect Service", string.Empty, [.. options]), (owner, picked) =>
			PromptConnect(owner, picked, static _ => { }));
		return CommandResult.Success("Choose a service to connect.");
	}

	private CommandResult DisconnectService(HostSession session, string? id) {
		if (id is not null) {
			// Deletes even an unreadable key, which counts as not connected.
			var definition = _credentials.Require(id);
			_credentials.Disconnect(id);
			return CommandResult.Success($"Disconnected {definition.DisplayName}.");
		}

		var options = _credentials.Definitions.Where(definition => _credentials.IsConnected(definition.Id))
			.Select(definition => ServiceOption(definition, null)).ToArray();
		if (options.Length == 0) {
			return CommandResult.Failure("No services are connected.");
		}

		ShowPicker(session, new ChoicePicker("Disconnect Service", string.Empty, options), (owner, picked) => {
			_credentials.Disconnect(picked);
			Notify(owner, "info", $"Disconnected {_credentials.Require(picked).DisplayName}.");
		});
		return CommandResult.Success("Choose a service to disconnect.");
	}

	/// <summary>Opens <paramref name="id"/>'s key page and asks <paramref name="session"/>'s page for the key; once it is
	/// saved, <paramref name="then"/> runs on that same session.</summary>
	private void PromptConnect(HostSession session, string id, Action<HostSession> then) {
		var definition = _credentials.Require(id);
		_pendingConnects[session.Address] = new PendingConnect(definition.Id, then);
		_ui.Post(() => _platform.OpenExternalUrl(definition.KeyPageUrl));
		session.State.Set("credentials", "prompt", "prompt", WireJson.Default.CredentialPrompt, new(
			definition.Id,
			definition.DisplayName,
			definition.KeyNoun,
			definition.KeyPageUrl));
	}

	// Every failure comes back as a result, so the still-open dialog shows it and never hangs on "Connecting…".
	private async Task<CredentialSaveResult> SaveCredentialAsync(HostSession session, string id, string key, CancellationToken ct) {
		if (string.IsNullOrWhiteSpace(key)) {
			return new CredentialSaveResult(false, "Paste a key to connect.");
		}

		var secret = new CredentialSecret(key.Trim());
		try {
			var definition = _credentials.Require(id);
			string account = await _credentials.ConnectAsync(definition.Id, secret, ct).ConfigureAwait(false);
			Notify(session, "info", $"Connected {definition.DisplayName}{(account.Length == 0 ? string.Empty : $" ({account})")}.");
			session.State.Remove("credentials", "prompt");
			if (_pendingConnects.TryRemove(session.Address, out var pending) && pending.Id == definition.Id) {
				pending.Then(session);
			}

			return new CredentialSaveResult(true, string.Empty);
		} catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException) {
			return new CredentialSaveResult(false, ex.Message);
		}
	}

	private void PublishConnectedLinkHosts() =>
		_messages.Host.Feature("sources").Publish("linkHosts", WireJson.Default.SourceLinkHosts, new([.. _sources.ConnectedLinkHosts()]));

	private static ChoiceOption ServiceOption(CredentialDefinition definition, string? group) =>
		new(definition.Id, definition.DisplayName, null, group);

	private static string? ServiceArg(string? argsJson) =>
		JsonSerializer.Deserialize(argsJson ?? "{}", WireJson.Default.CredentialServiceArgs)?.Service;

	private static CommandResult Guard(Func<CommandResult> run) {
		try {
			return run();
		} catch (Exception ex) when (ex is InvalidOperationException or JsonException or IOException or UnauthorizedAccessException) {
			return CommandResult.Failure(ex.Message);
		}
	}
}

internal sealed record CredentialServiceArgs(string? Service);

internal sealed record CredentialPrompt(string Id, string DisplayName, string KeyNoun, string KeyPageUrl);

internal sealed record SaveCredentialMessage(string Id, string Key);

internal sealed record CredentialSaveResult(bool Ok, string Error);
