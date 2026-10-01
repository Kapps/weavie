using System.Text.Json;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class NativeBridgeSecurityTests {
	[Theory]
	[InlineData(null, true, false)]
	[InlineData("not a URL", true, false)]
	[InlineData("about:blank", true, true)]
	[InlineData("about:blank", false, false)]
	[InlineData("https://app.test/index.html", true, false)]
	public void WithoutSelectedDocument_OnlyMainFrameBlankIsAllowed(string? url, bool mainFrame, bool expected) => Assert.Equal(expected, new NativeBridgeSecurity().Allows(url, mainFrame));

	[Theory]
	[InlineData("https://app.test/index.html?session=one", true, true)]
	[InlineData("https://app.test/index.html?session=one#anchor", true, true)]
	[InlineData("https://app.test/index.html", true, true)]
	[InlineData("https://app.test/index.html?session=two", true, false)]
	[InlineData("https://app.test/welcome.html", true, false)]
	[InlineData("https://user@app.test/index.html?session=one", true, false)]
	[InlineData("http://app.test/index.html?session=one", true, false)]
	[InlineData("https://app.test:444/index.html?session=one", true, false)]
	[InlineData("https://foreign.test/index.html?session=one", true, false)]
	[InlineData("data:text/html,untrusted", true, false)]
	[InlineData("about:blank", true, false)]
	[InlineData("https://app.test/index.html?session=one", false, false)]
	[InlineData("https://app.test/other.html", false, false)]
	[InlineData("https://user@app.test/index.html", false, false)]
	[InlineData("https://foreign.test/index.html", false, true)]
	[InlineData("http://app.test/index.html", false, true)]
	[InlineData("https://app.test:444/index.html", false, true)]
	[InlineData("data:text/html,untrusted", false, true)]
	public async Task SelectedDocument_RestrictsMainFrameAndExcludesSameOriginSubframes(string url, bool mainFrame, bool expected) {
		var security = new NativeBridgeSecurity();
		await LoadAsync(security, "https://app.test/index.html?session=one");

		Assert.Equal(expected, security.Allows(url, mainFrame));
	}

	[Theory]
	[InlineData("file:///app/index.html")]
	[InlineData("data:text/html,untrusted")]
	[InlineData("https://user@app.test/index.html")]
	public async Task LoadRejectsDocumentsWithoutAnExplicitTrustedOrigin(string url) {
		var security = new NativeBridgeSecurity();
		await Assert.ThrowsAsync<ArgumentException>(() => LoadAsync(security, url));
	}

	[Fact]
	public async Task RawAndForgedMessagesHaveNoAuthority() {
		var security = new NativeBridgeSecurity();
		await LoadAsync(security, "app://app/index.html");

		Assert.Null(security.Authenticate("{\"scope\":\"host\"}"));
		Assert.Null(security.Authenticate(new string('0', 64) + ":{\"scope\":\"host\"}"));
		Assert.Null(security.Authenticate(string.Empty));
	}

	[Fact]
	public async Task RevokeDuringScriptInstallationPreventsNavigationAndRemovesAuthority() {
		var security = new NativeBridgeSecurity();
		var installing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var installed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var navigated = new List<string>();
		string authenticatedMessage = string.Empty;
		var load = security.LoadAsync("app://app/index.html", string.Empty, "send", script => {
			string declaration = script.Split('\n').Single(line => line.TrimStart().StartsWith("const token = ", StringComparison.Ordinal));
			string token = JsonSerializer.Deserialize<string>(declaration.Trim()["const token = ".Length..^1])!;
			authenticatedMessage = token + ":message";
			installing.SetResult();
			return installed.Task;
		}, navigated.Add);
		await installing.Task;
		Assert.Equal("message", security.Authenticate(authenticatedMessage));

		security.Revoke();
		installed.SetResult();
		await load;

		Assert.Empty(navigated);
		Assert.False(security.Allows("app://app/index.html", true));
		Assert.True(security.Allows("about:blank", true));
		Assert.Null(security.Authenticate(authenticatedMessage));
	}

	[Fact]
	public async Task NewestLoadOwnsNavigationWhenAnEarlierInstallationIsPending() {
		var security = new NativeBridgeSecurity();
		var installing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var installed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var navigated = new List<string>();
		var load = security.LoadAsync("app://app/welcome.html", string.Empty, "send", _ => {
			installing.SetResult();
			return installed.Task;
		}, navigated.Add);
		await installing.Task;
		bool supersededScriptInstalled = false;
		var superseded = security.LoadAsync("app://app/superseded.html", string.Empty, "send", _ => {
			supersededScriptInstalled = true;
			return Task.CompletedTask;
		}, navigated.Add);
		var newest = security.LoadAsync("app://app/index.html", string.Empty, "send", _ => Task.CompletedTask, navigated.Add);

		installed.SetResult();
		await Task.WhenAll(load, superseded, newest);

		Assert.False(supersededScriptInstalled);
		Assert.Equal(["app://app/index.html"], navigated);
		Assert.True(security.Allows("app://app/index.html", true));
		Assert.False(security.Allows("app://app/welcome.html", true));
	}

	private static Task LoadAsync(NativeBridgeSecurity security, string url) =>
		security.LoadAsync(url, string.Empty, "send", _ => Task.CompletedTask, _ => { });
}
