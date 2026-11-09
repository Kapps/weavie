using Weavie.Core.Credentials;
using Weavie.Core.Sources;

namespace Weavie.Hosting;

/// <summary>The services Weavie reaches with a key the user pasted, over one shared client and credential store.</summary>
/// <param name="Sources">The document sources (Notion).</param>
/// <param name="Credentials">Every declared service's key.</param>
public sealed record ConnectedServices(ISourceConnector Sources, CredentialService Credentials);

/// <summary>Builds <see cref="ConnectedServices"/> for every host and the test host.</summary>
public static class ConnectedServicesComposition {
	/// <summary>Creates the connected services over <paramref name="http"/> and <paramref name="store"/>.</summary>
	public static ConnectedServices Create(HttpClient http, ICredentialStore store) {
		ArgumentNullException.ThrowIfNull(http);
		ArgumentNullException.ThrowIfNull(store);
		var notion = new NotionSource(http);
		var credentials = new CredentialService([notion.Credential], store);
		return new ConnectedServices(new SourceConnector([notion], credentials), credentials);
	}
}
