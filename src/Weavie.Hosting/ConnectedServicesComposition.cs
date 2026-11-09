using Weavie.Core.Configuration;
using Weavie.Core.Credentials;
using Weavie.Core.Inference;
using Weavie.Core.Inference.Decisions;
using Weavie.Core.Sources;

namespace Weavie.Hosting;

/// <summary>The services Weavie reaches with a key the user pasted, over one shared client and credential store.</summary>
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
		var credentials = new CredentialService([notion.Credential], store);
		var decisions = new DecisionService(settings, [new AgentDecisionProvider(inference)]);
		return new ConnectedServices(new SourceConnector([notion], credentials), credentials, decisions);
	}
}
