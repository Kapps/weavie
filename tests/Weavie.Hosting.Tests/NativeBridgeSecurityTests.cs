using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class NativeBridgeSecurityTests {
	[Theory]
	[InlineData("app://app/index.html")]
	[InlineData("https://weavie.dev/index.html")]
	[InlineData("http://127.0.0.1:1234/index.html")]
	public async Task OnlySelectedAppDocumentCanNavigateTheTopFrame(string document) {
		var security = new NativeBridgeSecurity();
		await security.LoadAsync(document, "", "body => {}", _ => Task.CompletedTask, _ => { });

		Assert.True(security.Allows(document, true));
		Assert.False(security.Allows("data:text/html,untrusted", true));
		Assert.False(security.Allows("https://example.com/index.html", true));
		Assert.False(security.Allows(document, false));
		Assert.False(security.Allows(document.Replace("://", "://user@"), false));
		Assert.True(security.Allows("https://example.com/index.html", false));
	}
}
