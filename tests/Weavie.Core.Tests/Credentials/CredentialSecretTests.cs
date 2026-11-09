using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.Credentials;
using Xunit;

namespace Weavie.Core.Tests.Credentials;

public sealed partial class CredentialSecretTests {
	[Fact]
	public void PrintsRedactedAndRevealsOnlyExplicitly() {
		var secret = new CredentialSecret("sk-live");
		Assert.Equal("[redacted]", secret.ToString());
		Assert.Equal("key [redacted]", $"key {secret}");
		Assert.Equal("sk-live", secret.Reveal());
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void RejectsAnEmptySecret(string value) =>
		Assert.ThrowsAny<ArgumentException>(() => new CredentialSecret(value));

	[Fact]
	public void RefusesSerializationDirectlyAndAsANestedProperty() {
		var secret = new CredentialSecret("sk-live");
		Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(secret, SecretJson.Default.CredentialSecret));
		Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new Holder(secret), SecretJson.Default.Holder));
		Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("""{"Key":"sk-live"}""", SecretJson.Default.Holder));
	}

	internal sealed record Holder(CredentialSecret Key);

	[JsonSerializable(typeof(Holder))]
	[JsonSerializable(typeof(CredentialSecret))]
	internal sealed partial class SecretJson : JsonSerializerContext;
}
