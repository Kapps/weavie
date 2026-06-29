using Weavie.Core.Sources;
using Xunit;

namespace Weavie.Core.Tests.Sources;

/// <summary>
/// Tests <see cref="SourceConnector"/>'s no-network paths: the setup URL lookup, and the loud failures for an
/// empty/unknown token target and an unconnected/unmatched fetch (validate + save + fetch happy paths run through
/// the Hosting integration tests, which stub the Notion API). These are the paths the host turns into the connect
/// prompt and the failure toasts.
/// </summary>
public sealed class SourceConnectorTests : IDisposable {
	private readonly string _dir = Path.Combine(Path.GetTempPath(), "weavie-source-connector", Guid.NewGuid().ToString("N"));
	private readonly SourceConnector _connector;

	public SourceConnectorTests() {
		Directory.CreateDirectory(_dir);
		_connector = new SourceConnector([new NotionSource(new HttpClient())], id => Path.Combine(_dir, $"{id}.json"));
	}

	public void Dispose() {
		try {
			Directory.Delete(_dir, recursive: true);
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
	}

	[Fact]
	public void SetupUrlFor_ReturnsTheSourcesTokenPage() =>
		Assert.Equal("https://app.notion.com/developers/tokens", _connector.SetupUrlFor(NotionSource.SourceId));

	[Fact]
	public void SetupUrlFor_UnknownSource_Throws() =>
		Assert.Throws<InvalidOperationException>(() => _connector.SetupUrlFor("dropbox"));

	[Fact]
	public async Task SaveTokenAsync_EmptyToken_Throws() =>
		await Assert.ThrowsAsync<ArgumentException>(() => _connector.SaveTokenAsync(NotionSource.SourceId, "  "));

	[Fact]
	public async Task SaveTokenAsync_UnknownSource_Throws() =>
		await Assert.ThrowsAsync<InvalidOperationException>(() => _connector.SaveTokenAsync("dropbox", "ntn_x"));

	[Fact]
	public async Task FetchAsync_NotConnected_ThrowsConnectFirst() {
		var ex = await Assert.ThrowsAsync<InvalidOperationException>(
			() => _connector.FetchAsync("https://www.notion.so/Spec-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d"));
		Assert.Contains("Connect", ex.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task FetchAsync_NoMatchingSource_Throws() =>
		await Assert.ThrowsAsync<InvalidOperationException>(() => _connector.FetchAsync("https://example.com/doc"));
}
