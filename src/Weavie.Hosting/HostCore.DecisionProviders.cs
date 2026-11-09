using System.Text.Json;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Inference.Decisions;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

// Choose Decision Model…: pick who answers workflow decisions. An unconnected vendor is connected first, and the
// setting changes only once that key is saved; disconnecting the chosen vendor hands decisions back to the agent.
public sealed partial class HostCore {
	private void RegisterDecisionProviderHandler(HostSession session) =>
		session.Commands.RegisterHandler(CoreCommands.ChooseDecisionProvider, (argsJson, _) =>
			Task.FromResult(Guard(() => ChooseDecisionProvider(session, ProviderArg(argsJson)))));

	private CommandResult ChooseDecisionProvider(HostSession session, string? id) {
		if (id is not null) {
			return UseDecisionProvider(session, id);
		}

		var options = _decisions.Providers.Select(provider => new ChoiceOption(
			provider.Id, provider.Label, provider.Detail, provider.Credential is { } credential ? ConnectionGroup(credential.Id) : null));
		string current = _settings.RequireString(InferenceSettings.DecisionProvider);
		ShowPicker(session, new ChoicePicker("Choose Decision Model", current, ConnectedFirst(options)),
			(owner, picked) => NotifyResult(owner, () => UseDecisionProvider(owner, picked)));
		return CommandResult.Success("Choose who answers workflow decisions.");
	}

	private CommandResult UseDecisionProvider(HostSession session, string id) {
		var provider = _decisions.Providers.FirstOrDefault(candidate => candidate.Id == id)
			?? throw new InvalidOperationException(
				$"No decision provider named '{id}' — use one of {string.Join(", ", _decisions.Providers.Select(p => p.Id))}.");
		if (provider.Credential is { } credential && !_credentials.IsConnected(credential.Id)) {
			PromptConnect(session, credential.Id, owner => NotifyResult(owner, () => SetDecisionProvider(provider)));
			return CommandResult.Success($"Connect {credential.DisplayName} to use it for workflow decisions.");
		}

		return SetDecisionProvider(provider);
	}

	private CommandResult SetDecisionProvider(IDecisionProvider provider) {
		var element = JsonSerializer.SerializeToElement(provider.Id, WireJson.Default.String);
		if (_settings.Set(InferenceSettings.DecisionProvider, element).ShadowedByEnv is { Length: > 0 } variable) {
			return CommandResult.Failure($"{variable} overrides {InferenceSettings.DecisionProvider}; unset it to choose a decision model.");
		}

		return CommandResult.Success(_settings.RequireBool(InferenceSettings.Enabled)
			? $"Workflow decisions now use {provider.Label}."
			: $"Workflow decisions will use {provider.Label}, but ad-hoc inference is off — turn on {InferenceSettings.Enabled} to run them.");
	}

	// Disconnecting the chosen vendor resets the choice, so the setting never names a disconnected service.
	private string DisconnectAndReset(string id) {
		_credentials.Disconnect(id);
		string current = _settings.RequireString(InferenceSettings.DecisionProvider);
		if (!_decisions.Providers.Any(provider => provider.Id == current && provider.Credential?.Id == id)) {
			return string.Empty;
		}

		var reset = SetDecisionProvider(_decisions.Providers.First(provider => provider.Id == DecisionProviderIds.Agent));
		return $" {(reset.Ok ? reset.Message : reset.Error)}";
	}

	private static string? ProviderArg(string? argsJson) =>
		JsonSerializer.Deserialize(argsJson ?? "{}", WireJson.Default.DecisionProviderArgs)?.Provider;
}

internal sealed record DecisionProviderArgs(string? Provider);
