using Weavie.Core.Credentials;
using Xunit;

namespace Weavie.Core.Tests.Credentials;

public sealed class CredentialServiceTests : IDisposable {
	private readonly TempDirectory _dir = new("weavie-credential-service");

	public void Dispose() => _dir.Dispose();

	[Fact]
	public void UnreadableKeyCountsAsNotConnectedButReadThrows() {
		var service = new CredentialService([new CredentialDefinition {
			Id = "acme",
			DisplayName = "Acme",
			KeyNoun = "API key",
			KeyPageUrl = "https://acme.example/keys",
			ValidateAsync = (_, _) => Task.FromResult(string.Empty),
		}], new FileCredentialStore(_dir.Path));
		Directory.CreateDirectory(_dir.Combine("acme.key"));

		Assert.False(service.IsConnected("acme"));
		Assert.Throws<InvalidOperationException>(() => service.Read("acme"));
	}
}
