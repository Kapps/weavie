using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpQuestionPresentationTests {
	[Theory]
	[InlineData("Latest reasoning summary as one compact status row (recommended)")]
	[InlineData("All thinking since my latest message, as in mockup C")]
	[InlineData("Keep both in a compact history")]
	public async Task TitleOnlyQuestionKeepsRequestContextSeparateAndReturnsTheAnswer(string answer) {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: false, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("input-question");
		var request = await fixture.WaitForMessageAsync(message => message.Type == "input-requested");
		Assert.Equal("Codex keeps working while you answer. An answer sent after it finishes is not delivered.", request.Summary);
		var question = Assert.Single(request.Questions!);
		Assert.Equal("Given that distinction, what should “Current” show? Actual message replies will stay visible in either case.", question.Header);
		Assert.Empty(question.Question);
		Assert.True(question.AllowsOther);
		Assert.False(question.Required);
		Assert.Equal(2, question.Options.Count);
		Assert.All(question.Options, option => {
			Assert.Equal(option.Value, option.Label);
			Assert.Empty(option.Description);
		});
		fixture.Session.ResolveInput(request.RequestId!, "accept", new Dictionary<string, IReadOnlyList<string>> {
			["question_0"] = [answer],
		});
		var resolved = await fixture.WaitForMessageAsync(message => message.Type == "input-resolved");
		Assert.Equal([answer], resolved.Answers!["question_0"]);
		var reply = await fixture.WaitForMessageAsync(message => message.Text?.StartsWith("question response: ", StringComparison.Ordinal) == true);
		using var response = System.Text.Json.JsonDocument.Parse(reply.Text!["question response: ".Length..]);
		Assert.Equal("accept", response.RootElement.GetProperty("action").GetString());
		Assert.Equal(answer, response.RootElement.GetProperty("content").GetProperty("question_0").GetString());
	}

	[Fact]
	public async Task FieldDescriptionAndRequestContextRemainDistinct() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: false, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("input");
		var request = await fixture.WaitForMessageAsync(message => message.Type == "input-requested");
		Assert.Equal("Choose a value", request.Summary);
		var question = Assert.Single(request.Questions!);
		Assert.Equal("Choice", question.Header);
		Assert.Equal("Which value?", question.Question);
		fixture.Session.ResolveInput(request.RequestId!, "accept", new Dictionary<string, IReadOnlyList<string>> {
			["choice"] = ["one"],
		});
		await fixture.WaitForMessageAsync(message => message.Text == "input: one");
	}
}
