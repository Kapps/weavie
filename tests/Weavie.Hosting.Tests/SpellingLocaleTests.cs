using System.Text.Json;
using Weavie.Core.Spelling;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>The page names the dictionary on every spelling request; the host holds no locale of its own.</summary>
[Collection(TestCollections.HostIntegration)]
public sealed class SpellingLocaleTests {
	[Fact]
	public async Task EachCheckAndSuggestionIsAnsweredInTheLocaleItNames() {
		await using var host = await TestHost.StartAsync();
		using var server = new FakeDictionaryServer();
		using var http = new HttpClient(server);
		using (var installed = new SpellLanguages(Path.GetDirectoryName(host.Settings.FilePath)!, http)) {
			await installed.ForAsync("fr", CancellationToken.None);
		}

		var spans = new[] { new { line = 1, offset = 0, text = "color", identifier = false } };
		Task<JsonElement> Request(string name, object payload) =>
			host.SessionRequestAsync<JsonElement>(host.SelectedSession, "spelling", name, payload);

		Assert.Equal(0, (await Request("check", new { spans, locale = "en" })).GetArrayLength());
		Assert.Equal("color", (await Request("check", new { spans, locale = "fr" }))[0].GetProperty("word").GetString());
		var suggestions = await Request("suggest", new { word = "colur", locale = "fr" });
		Assert.Contains("colour", suggestions.EnumerateArray().Select(suggestion => suggestion.GetString()));
		Assert.Equal("en", host.Settings.RequireString(Weavie.Core.Configuration.EditorSettings.SpellCheckLocale));
	}
}
