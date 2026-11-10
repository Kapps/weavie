using Weavie.Core.Configuration;
using Weavie.Core.Credentials;
using Weavie.Core.Inference;
using Weavie.Core.Inference.Decisions;
using Weavie.Core.Sources;

namespace Weavie.Hosting;

/// <summary>The services Weavie reaches with a key the user pasted (Notion, OpenAI, TypeSafe), over one shared client and credential store.</summary>
/// <param name="Sources">The document sources (Notion).</param>
/// <param name="Credentials">Every declared service's key.</param>
/// <param name="Decisions">The decision providers, selected live by <c>inference.decisionProvider</c>.</param>
public sealed record ConnectedServices(ISourceConnector Sources, CredentialService Credentials, IDecisionService Decisions);

/// <summary>Builds <see cref="ConnectedServices"/> for every host and the test host.</summary>
public static class ConnectedServicesComposition {
	/// <summary>Creates the connected services over <paramref name="http"/> and <paramref name="store"/>.</summary>
	public static ConnectedServices Create(
		HttpClient http, ICredentialStore store, SettingsStore settings, IInferenceService inference) {
		ArgumentNullException.ThrowIfNull(http);
		ArgumentNullException.ThrowIfNull(store);
		var notion = new NotionSource(http);
		IDecisionProvider[] providers = [
			new AgentDecisionProvider(inference),
			new OpenAiDecisionProvider(http, store),
			new TypeSafeDecisionProvider(http, store),
		];
		if (!providers.Select(provider => provider.Id).SequenceEqual(DecisionProviderIds.All)) {
			throw new InvalidOperationException("The decision providers must match DecisionProviderIds.All, which the setting accepts.");
		}
		var credentials = new CredentialService(
			[notion.Credential, .. providers.Select(provider => provider.Credential).OfType<CredentialDefinition>()], store);
		var decisions = new DecisionService(settings, providers);
		return new ConnectedServices(new SourceConnector([notion], credentials), credentials, decisions);
	}
}
