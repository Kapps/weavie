using System.Text.Json;
using Weavie.AgentClientProtocol;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpContentAnnotationsTests {
	[Theory]
	[InlineData("{}", true)]
	[InlineData("{\"annotations\":null}", true)]
	[InlineData("{\"annotations\":{}}", true)]
	[InlineData("{\"annotations\":{\"audience\":null}}", true)]
	[InlineData("{\"annotations\":{\"priority\":0}}", true)]
	[InlineData("{\"annotations\":{\"audience\":[]}}", false)]
	[InlineData("{\"annotations\":{\"audience\":[\"assistant\"]}}", false)]
	[InlineData("{\"annotations\":{\"audience\":[\"user\"]}}", true)]
	[InlineData("{\"annotations\":{\"audience\":[\"assistant\",\"user\"]}}", true)]
	public void VisibilityFollowsTheDeclaredAudience(string json, bool expected) {
		using var document = JsonDocument.Parse(json);
		Assert.Equal(expected, AcpContentAnnotations.IsUserVisible(document.RootElement));
	}

	[Theory]
	[InlineData("{\"annotations\":[]}")]
	[InlineData("{\"annotations\":{\"audience\":\"assistant\"}}")]
	[InlineData("{\"annotations\":{\"audience\":[false]}}")]
	[InlineData("{\"annotations\":{\"audience\":[null]}}")]
	[InlineData("{\"annotations\":{\"audience\":[\"system\"]}}")]
	[InlineData("{\"annotations\":{\"audience\":[\"user\",42]}}")]
	public void MalformedAudienceFailsInsteadOfSilentlyHidingContent(string json) {
		using var document = JsonDocument.Parse(json);
		Assert.Throws<AcpProtocolException>(() => AcpContentAnnotations.IsUserVisible(document.RootElement));
	}
}
