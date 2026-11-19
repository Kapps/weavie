using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.Spelling;
using Xunit;

namespace Weavie.Core.Tests;

[Collection("Settings")]
public sealed class SpellLanguagesTests : IDisposable {
	private readonly TempDirectory _directory = new("weavie-spell-languages");
	private readonly FakeDictionaryServer _server = new();
	private readonly HttpClient _http;

	public SpellLanguagesTests() {
		_http = new HttpClient(_server);
	}

	private SpellLanguages Languages() => new(_directory.Path, _http);

	private SettingsStore Settings() => CoreSettings.CreateStore(_directory.Combine("settings.toml"), enableWatcher: false);

	private Task<SpellVocabulary> French(SpellLanguages languages) => languages.ForAsync("fr", CancellationToken.None);

	[Fact]
	public async Task InstallsALocaleWhenFirstAskedAndReusesItOffline() {
		using var languages = Languages();
		var english = await languages.ForAsync("en", CancellationToken.None);
		Assert.True(english.Check("color"));
		Assert.True(english.Check("colour"));
		Assert.Empty(_server.Requests);
		var french = await French(languages);
		Assert.True(french.Check("colour"));
		Assert.False(french.Check("color"));
		Assert.False(french.Check("middleware"));
		Assert.Same(french, await French(languages));
		Assert.Equal(3, _server.Requests.Count);
		Assert.Equal("test license", File.ReadAllText(Directory.GetFiles(_directory.Path, "license", SearchOption.AllDirectories).Single()));
		_server.OnRequest = () => throw new HttpRequestException("offline");
		using var offline = Languages();
		Assert.Contains("colour", (await French(offline)).Suggest("colur"));
		Assert.Equal(3, _server.Requests.Count);
	}

	[Fact]
	public async Task RegionalEnglishDownloadsFromUpstreamAndKeepsTechnicalWords() {
		using var languages = Languages();
		var regional = await languages.ForAsync("en-US", CancellationToken.None);
		Assert.Equal(3, _server.Requests.Count);
		Assert.All(_server.Requests, path => Assert.Contains("/dictionaries/en/", path));
		Assert.True(regional.Check("middleware"));
	}

	[Fact]
	public async Task ConcurrentRequestsShareOneInstallThatOutlivesACancelledCaller() {
		var release = new TaskCompletionSource();
		_server.Gate = release.Task;
		using var languages = Languages();
		using var cancelled = new CancellationTokenSource();
		var abandoned = languages.ForAsync("fr", cancelled.Token);
		var waiting = French(languages);
		cancelled.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
		release.SetResult();
		Assert.True((await waiting).Check("colour"));
		Assert.Equal(3, _server.Requests.Count);
	}

	[Theory]
	[InlineData("index.dic", false)]
	[InlineData("license", false)]
	[InlineData("index.dic", true)]
	public async Task AFailedInstallFailsTheRequestPublishesNothingAndIsRetried(string failedFile, bool empty) {
		using var failing = new FakeDictionaryServer { FailedFile = failedFile, Empty = empty };
		using var http = new HttpClient(failing);
		using var languages = new SpellLanguages(_directory.Path, http);
		using var settings = Settings();
		await Assert.ThrowsAnyAsync<Exception>(() => French(languages));
		Assert.False((await languages.SetLocaleAsync(settings, "{\"locale\":\"fr\"}", CancellationToken.None)).Ok);
		Assert.Equal("en", settings.RequireString(EditorSettings.SpellCheckLocale));
		Assert.Empty(Directory.GetFiles(_directory.Path, "index.*", SearchOption.AllDirectories));
		failing.FailedFile = "";
		Assert.True((await French(languages)).Check("colour"));
	}

	[Fact]
	public async Task InvalidLocaleNeverReachesTheCacheOrTheSetting() {
		using var languages = Languages();
		using var settings = Settings();
		await Assert.ThrowsAsync<ArgumentException>(() => languages.ForAsync("../outside", CancellationToken.None));
		Assert.False((await languages.SetLocaleAsync(settings, "{\"locale\":\"../outside\"}", CancellationToken.None)).Ok);
		Assert.Throws<SettingValidationException>(() => settings.Set(EditorSettings.SpellCheckLocale, JsonSerializer.SerializeToElement("../outside")));
		Assert.Empty(_server.Requests);
	}

	[Fact]
	public async Task SetLocaleListsUpstreamCodesAndSavesAnInstalledLocale() {
		using var languages = Languages();
		using var settings = Settings();
		var catalogue = await languages.SetLocaleAsync(settings, null, CancellationToken.None);
		using var data = JsonDocument.Parse(catalogue.DataJson!);
		Assert.Equal(["en", "en-US", "fr"], data.RootElement.GetProperty("locales").EnumerateArray().Select(value => value.GetString()));
		var result = await languages.SetLocaleAsync(settings, "{\"locale\":\"fr\"}", CancellationToken.None);
		Assert.True(result.Ok, result.Error);
		Assert.Equal("fr", settings.RequireString(EditorSettings.SpellCheckLocale));
		Assert.Empty(SpellChecker.Check(await French(languages), [new(1, 0, "école", false)], new HashSet<string>(), new HashSet<string>(), CancellationToken.None));
	}

	public void Dispose() {
		_http.Dispose();
		_server.Dispose();
		_directory.Dispose();
	}
}
