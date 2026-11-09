using Weavie.Core.Credentials;
using Xunit;

namespace Weavie.Core.Tests.Credentials;

public sealed class CredentialServiceTests : IDisposable {
	private readonly TempDirectory _dir = new("weavie-credential-service");
	private readonly FileCredentialStore _store;
	private readonly CredentialService _service;
	private readonly List<string> _validated = [];
	private int _changes;

	public CredentialServiceTests() {
		_store = new FileCredentialStore(_dir.Path);
		_service = new CredentialService([new CredentialDefinition {
			Id = "acme",
			DisplayName = "Acme",
			KeyNoun = "API key",
			KeyPageUrl = "https://acme.example/keys",
			ValidateAsync = (key, _) => {
				_validated.Add(key.Reveal());
				return key.Reveal() == "good"
					? Task.FromResult("team")
					: Task.FromException<string>(new InvalidOperationException("Acme rejected the key."));
			},
		}], _store);
		_service.Changed += () => _changes++;
	}

	public void Dispose() => _dir.Dispose();

	[Fact]
	public async Task ConnectValidatesThenSavesAndReturnsTheAccount() {
		Assert.Equal("team", await _service.ConnectAsync("acme", new CredentialSecret("good"), CancellationToken.None));

		Assert.Equal(["good"], _validated);
		Assert.True(_service.IsConnected("acme"));
		Assert.Equal("good", _store.Read("acme")!.Reveal());
		Assert.Equal(1, _changes);
	}

	[Fact]
	public async Task RejectedKeyIsNeverSaved() {
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => _service.ConnectAsync("acme", new CredentialSecret("bad"), CancellationToken.None));

		Assert.False(_service.IsConnected("acme"));
		Assert.Equal(0, _changes);
	}

	[Fact]
	public async Task DisconnectDeletesTheKey() {
		await _service.ConnectAsync("acme", new CredentialSecret("good"), CancellationToken.None);

		_service.Disconnect("acme");

		Assert.False(_service.IsConnected("acme"));
		Assert.Equal(2, _changes);
	}

	[Fact]
	public void UnreadableKeyCountsAsNotConnected() {
		Directory.CreateDirectory(_dir.Combine("acme.key"));

		Assert.False(_service.IsConnected("acme"));
		Assert.Throws<InvalidOperationException>(() => _service.Read("acme"));
	}

	[Fact]
	public async Task UndeclaredServiceThrows() {
		Assert.Throws<InvalidOperationException>(() => _service.Require("other"));
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => _service.ConnectAsync("other", new CredentialSecret("good"), CancellationToken.None));
	}
}
