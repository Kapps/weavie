using System.Text.Json;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Credentials;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

// Connect Service… / Disconnect Service… for every declared credential; a connect runs its continuation once the key is saved.
public sealed partial class HostCore {
	private const string NotConnected = "Not connected";

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
			session.ConnectPrompt.Take();
			return Task.CompletedTask;
		});
		WirePicker(session);
	}

	private CommandResult ConnectService(HostSession session, string? id) {
		if (id is not null) {
			PromptConnect(session, id, static _ => { });
		} else {
			var options = _credentials.Definitions.Select(definition => ServiceOption(definition, ConnectionGroup(definition.Id)));
			ShowPicker(session, new ChoicePicker("Connect Service", string.Empty, ConnectedFirst(options)),
				(owner, picked) => ConnectService(owner, picked));
		}
		return CommandResult.Success();
	}

	private CommandResult DisconnectService(HostSession session, string? id) {
		if (id is not null) {
			var definition = _credentials.Require(id);
			return DisconnectAndReset(id) is { } reset
				? CommandResult.Success($"Disconnected {definition.DisplayName}.{reset}")
				: CommandResult.Success($"{definition.DisplayName} isn't connected.");
		}

		var options = _credentials.Definitions.Where(definition => _credentials.IsConnected(definition.Id))
			.Select(definition => ServiceOption(definition, null)).ToArray();
		if (options.Length == 0) {
			return CommandResult.Failure("No services are connected.");
		}

		ShowPicker(session, new ChoicePicker("Disconnect Service", string.Empty, options), (owner, picked) =>
			NotifyResult(owner, () => DisconnectService(owner, picked)));
		return CommandResult.Success();
	}

	/// <summary>Opens <paramref name="id"/>'s key page and asks <paramref name="session"/>'s page for the key; once it is
	/// saved, <paramref name="then"/> runs on that same session.</summary>
	private void PromptConnect(HostSession session, string id, Action<HostSession> then) {
		var definition = _credentials.Require(id);
		session.ConnectPrompt.Set(new PendingConnect(definition.Id, then));
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
			if (session.ConnectPrompt.Take() is { } pending && pending.Id == definition.Id) {
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

	private string ConnectionGroup(string id) => _credentials.IsConnected(id) ? "Connected" : NotConnected;

	// Options needing no key keep their place ahead of the rest; connected services come before unconnected ones.
	private static ChoiceOption[] ConnectedFirst(IEnumerable<ChoiceOption> options) =>
		[.. options.OrderBy(option => option.Group == NotConnected)];

	// A command finished from a picker or connect prompt has no caller to read its result, so a message becomes a toast.
	private static void NotifyResult(HostSession session, Func<CommandResult> run) {
		var result = Guard(run);
		if ((result.Ok ? result.Message : result.Error) is { } message) {
			Notify(session, result.Ok ? "info" : "warn", message);
		}
	}

	private static string? ServiceArg(string? argsJson) =>
		JsonSerializer.Deserialize(argsJson ?? "{}", WireJson.Default.CredentialServiceArgs)?.Service;

	private static CommandResult Guard(Func<CommandResult> run) {
		try {
			return run();
		} catch (Exception ex) when (ex is InvalidOperationException or JsonException or IOException or UnauthorizedAccessException
			or SettingValidationException or SettingsFileMalformedException) {
			return CommandResult.Failure(ex.Message);
		}
	}
}

internal sealed record PendingConnect(string Id, Action<HostSession> Then);

internal sealed record CredentialServiceArgs(string? Service);

internal sealed record CredentialPrompt(string Id, string DisplayName, string KeyNoun, string KeyPageUrl);

internal sealed record SaveCredentialMessage(string Id, string Key);

internal sealed record CredentialSaveResult(bool Ok, string Error);
