using Weavie.AgentClientProtocol;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpPromptContextTests {
	private const string Guidance = "weavie://instructions\n<context ref=\"weavie://instructions\">\nhidden guidance\n</context>";
	private const string Selection = "[@file.cs#selection](file:///workspace/file.cs#selection)\n<context ref=\"file:///workspace/file.cs#selection\">\n<item>selected code</item>\n</context>";

	[Theory]
	[InlineData(Guidance, "")]
	[InlineData("\n" + Guidance + "\n", "")]
	[InlineData("prompt" + Guidance, "prompt")]
	[InlineData("prompt\n\n" + Guidance, "prompt\n\n")]
	[InlineData("prompt  " + Guidance, "prompt  ")]
	[InlineData("prompt" + Guidance + Selection, "prompt")]
	[InlineData("prompt" + Guidance + "\n\n" + Selection, "prompt")]
	[InlineData(Selection, "")]
	public void RemovesOnlyAppendedOwnedEnvelopes(string text, string expected) =>
		Assert.Equal(expected, AcpPromptContext.RemoveInjectedText(text));

	[Theory]
	[InlineData("<context ref=\"weavie://instructions\">\nuser XML\n</context>")]
	[InlineData("<item>user XML</item>")]
	[InlineData("Explain " + Guidance + " after this")]
	[InlineData("```xml\n" + Guidance + "\n```")]
	[InlineData("```xml\n" + Guidance)]
	[InlineData("~~~~xml\n~~~\n" + Guidance)]
	[InlineData("https://example.test#selection\n<context ref=\"https://example.test#selection\">\nuser resource\n</context>")]
	[InlineData("[@file.cs](file:///workspace/file.cs)\n<context ref=\"file:///workspace/file.cs\">\nuser resource\n</context>")]
	[InlineData("weavie://instructions\n<context ref=\"weavie://instructions\">\nincomplete")]
	public void PreservesUserContent(string text) =>
		Assert.Equal(text, AcpPromptContext.RemoveInjectedText(text));

	[Fact]
	public void RemovesContextAfterAClosedFenceWithoutChangingTheExample() {
		string example = "````xml\n```\n" + Guidance + "\n````\n";
		Assert.Equal(example, AcpPromptContext.RemoveInjectedText(example + Guidance));
	}

	[Theory]
	[InlineData("<context ref=\"https://example.test\">\nselected XML\n</context>")]
	[InlineData("<context ref=\"weavie://instructions\">\nselected XML\n</context>")]
	[InlineData("<context ref=\"unfinished selected code")]
	public void SelectedContextTagsRemainInsideTheOpaqueEnvelope(string selection) {
		string text = "prompt" + Guidance + Selection.Replace("<item>selected code</item>", selection, StringComparison.Ordinal);
		Assert.Equal("prompt", AcpPromptContext.RemoveInjectedText(text));
	}

	[Fact]
	public void DoesNotConsumeAnUnownedEnvelopeAfterAnOwnedEnvelope() {
		string text = Guidance + "https://example.test\n<context ref=\"https://example.test\">\nuser resource\n</context>";
		Assert.Equal(text, AcpPromptContext.RemoveInjectedText(text));
	}

	[Theory]
	[InlineData("weavie://instructions", true)]
	[InlineData("file:///workspace/file.cs#selection", true)]
	[InlineData("https://example.test#selection", false)]
	[InlineData("file:///workspace/file.cs#selection-other", false)]
	[InlineData("file:///workspace/file.cs", false)]
	[InlineData("weavie://instructions-other", false)]
	public void RecognizesOnlyWeavieInjectedResourceUris(string uri, bool expected) =>
		Assert.Equal(expected, AcpPromptContext.IsInjectedUri(uri));
}
