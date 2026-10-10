using Weavie.Core.Review.Hints;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>An ACP agent reports review findings through the session registry's addReviewHint tool.</summary>
public sealed class AcpReviewHintTests {
	[Fact]
	public async Task NativeSession_AddsAReviewHintThroughTheRegistry() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		File.WriteAllText(Path.Combine(fixture.Workspace, "a.cs"), "one\ntwo\n");
		await fixture.StartAsync();

		fixture.Submit("""review-hint:{"path":"a.cs","line":2,"severity":"nit","text":"Rename two"}""");

		await fixture.WaitForMessageAsync(message => message.Text?.StartsWith("review hint: Hint added at a.cs:2", StringComparison.Ordinal) == true);
		var hint = Assert.Single(fixture.ReviewHints.Hints);
		Assert.Equal(("a.cs", 2, ReviewHintSeverity.Nit, "Rename two", "two"), (hint.Path, hint.Line, hint.Severity, hint.Title, hint.Quote));
	}
}
