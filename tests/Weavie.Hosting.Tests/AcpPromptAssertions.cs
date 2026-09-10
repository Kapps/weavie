using System.Text.Json;
using Weavie.Core.Mcp;
using Xunit;

namespace Weavie.Hosting.Tests;

internal static class AcpPromptAssertions {
	public static JsonElement[] Read(AcpAgentSessionFixture fixture) => [.. File.ReadAllLines(
		Path.Combine(fixture.FakeAcpStateDirectory, "wire-prompts.jsonl"))
		.Select(line => JsonSerializer.Deserialize<JsonElement>(line))];

	public static JsonElement.ArrayEnumerator Blocks(JsonElement request) =>
		request.GetProperty("parameters").GetProperty("prompt").EnumerateArray();

	public static void SideScope(JsonElement request, string userText) {
		string?[] text = [.. Blocks(request).Where(block => block.GetProperty("type").GetString() == "text")
			.Select(block => block.GetProperty("text").GetString())];
		Assert.Single(text, value => value == EmbeddedAgentGuidance.SideConversationInstructions);
		Assert.Equal(userText.Length == 0 ? [] : new[] { userText },
			text.Where(value => value != EmbeddedAgentGuidance.SideConversationInstructions));
	}
}
