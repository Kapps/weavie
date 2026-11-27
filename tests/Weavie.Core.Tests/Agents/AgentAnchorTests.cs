using Weavie.Core.Agents;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class AgentAnchorTests {
	[Fact]
	public void AnAnchorNamesAFileOrAWalkthroughWithAShortPlaceAndAOneLineQuote() {
		Assert.Null(new AgentAnchor("/w/a.cs", null, null, "3.1-4.9", "int b;", "ask-1").Problem());
		Assert.Null(new AgentAnchor(null, null, "claims", "one/code/0/2@abc", "int b;", "ask-1").Problem());

		Assert.Equal("An anchor names a file or a walkthrough.", new AgentAnchor(null, null, null, "gist", "g", "ask-1").Problem());
		Assert.Equal("An anchor names a file or a walkthrough.", new AgentAnchor("/w/a.cs", null, "claims", "gist", "g", "ask-1").Problem());
		Assert.Equal("An anchor's file needs a path.", new AgentAnchor("", null, null, "1.1-1.2", "g", "ask-1").Problem());
		Assert.NotNull(new AgentAnchor(null, null, "claims", "not a place", "g", "ask-1").Problem());
		Assert.NotNull(new AgentAnchor(null, null, "claims", "gist", "g", "").Problem());
		Assert.NotNull(new AgentAnchor(null, null, "claims", "gist", "two\nlines", "ask-1").Problem());
		Assert.NotNull(new AgentAnchor(null, null, "claims", "gist", new string('x', AgentAnchor.QuoteLength + 1), "ask-1").Problem());
	}
}
