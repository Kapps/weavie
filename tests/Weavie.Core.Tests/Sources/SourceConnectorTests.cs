using Weavie.Core.Credentials;
using Weavie.Core.Sources;
using Xunit;

namespace Weavie.Core.Tests.Sources;

/// <summary>
/// Tests <see cref="SourceConnector"/>'s no-network paths over a temp credential store: the loud failures for an
/// unconnected/unmatched fetch and which link hosts count as connected (fetch happy paths run through the Hosting
/// integration tests, which stub the Notion API).
/// </summary>
public sealed class SourceConnectorTests : IDisposable {
	private readonly TempDirectory _dir = new("weavie-source-connector");
	private readonly FileCredentialStore _store;
	private readonly SourceConnector _connector;

	public SourceConnectorTests() {
		_store = new FileCredentialStore(_dir.Path);
		var notion = new NotionSource(new HttpClient());
		_connector = new SourceConnector([notion], new CredentialService([notion.Credential], _store));
	}

	public void Dispose() => _dir.Dispose();

	[Fact]
	public async Task FetchAsync_NotConnected_ThrowsConnectFirst() {
		var ex = await Assert.ThrowsAsync<InvalidOperationException>(
			() => _connector.FetchAsync("https://www.notion.so/Spec-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d"));
		Assert.Contains("Connect Notion", ex.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task FetchAsync_NoMatchingSource_Throws() =>
		await Assert.ThrowsAsync<InvalidOperationException>(() => _connector.FetchAsync("https://example.com/doc"));

	[Fact]
	public void ConnectedLinkHosts_ListsOnlySourcesWithASavedToken() {
		Assert.Empty(_connector.ConnectedLinkHosts());
		_store.Save(NotionSource.SourceId, new CredentialSecret("ntn_x"));
		Assert.Equal(NotionSource.Hosts, _connector.ConnectedLinkHosts());
		Assert.True(_connector.IsConnected("https://www.notion.so/Spec-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d"));
	}
}
