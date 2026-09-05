using System.Net;
using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.Spelling;
using Xunit;

namespace Weavie.Core.Tests;

[Collection("Settings")]
public sealed class SpellLanguagesTests : IDisposable {
	private readonly TempDirectory _directory = new("weavie-spell-languages");
	private string SettingsPath => _directory.Combine("settings.toml");

	[Fact]
	public async Task InstallsBeforeActivationAndReusesPersistedDictionaryOffline() {
		using var settings = CoreSettings.CreateStore(SettingsPath, enableWatcher: false);
		using var handler = new DictionaryServer();
		using var http = new HttpClient(handler);
		using var languages = new SpellLanguages(settings, http);
		Assert.True(languages.Current.Check("color"));
		Assert.False(languages.Current.Check("colour"));
		handler.OnRequest = () => Assert.Equal("en-US", settings.RequireString(EditorSettings.SpellCheckLocale));
		var result = await languages.SetLocaleAsync("{\"locale\":\"en-GB\"}", CancellationToken.None);
		Assert.True(result.Ok, result.Error);
		Assert.True(languages.Current.Check("colour"));
		Assert.False(languages.Current.Check("color"));
		Assert.Equal(3, handler.Requests.Count);
		Assert.Equal("test license", File.ReadAllText(Directory.GetFiles(_directory.Path, "license", SearchOption.AllDirectories).Single()));
		handler.OnRequest = () => throw new HttpRequestException("offline");
		using var reloaded = CoreSettings.CreateStore(SettingsPath, enableWatcher: false);
		using var offline = new SpellLanguages(reloaded, http);
		Assert.True(offline.Current.Check("colour"));
		Assert.True((await offline.SetLocaleAsync("{\"locale\":\"en-US\"}", CancellationToken.None)).Ok);
		Assert.True((await offline.SetLocaleAsync("{\"locale\":\"en-GB\"}", CancellationToken.None)).Ok);
		Assert.Equal(3, handler.Requests.Count);
		Assert.Contains("colour", offline.Current.Suggest("colur"));
	}

	[Theory]
	[InlineData("index.dic", false)]
	[InlineData("license", false)]
	[InlineData("index.dic", true)]
	public async Task FailedOrEmptyDownloadNeverChangesLocaleOrPublishesCache(string failedFile, bool empty) {
		using var settings = CoreSettings.CreateStore(SettingsPath, enableWatcher: false);
		using var handler = new DictionaryServer { FailedFile = failedFile, Empty = empty };
		using var http = new HttpClient(handler);
		using var languages = new SpellLanguages(settings, http);
		var result = await languages.SetLocaleAsync("{\"locale\":\"en-GB\"}", CancellationToken.None);
		Assert.False(result.Ok);
		Assert.Equal("en-US", settings.RequireString(EditorSettings.SpellCheckLocale));
		Assert.Empty(Directory.GetFiles(_directory.Path, "index.*", SearchOption.AllDirectories));
		Assert.True(languages.Current.Check("color"));
	}

	[Fact]
	public async Task CancellationCleansStagingAndAllowsAnotherSwitch() {
		using var settings = CoreSettings.CreateStore(SettingsPath, enableWatcher: false);
		using var cancelled = new CancellationTokenSource();
		using var handler = new DictionaryServer { OnRequest = () => cancelled.Cancel() };
		using var http = new HttpClient(handler);
		using var languages = new SpellLanguages(settings, http);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => languages.SetLocaleAsync("{\"locale\":\"en-GB\"}", cancelled.Token));
		Assert.Equal("en-US", settings.RequireString(EditorSettings.SpellCheckLocale));
		Assert.Empty(Directory.GetFiles(_directory.Path, "index.*", SearchOption.AllDirectories));
		handler.OnRequest = () => { };
		Assert.True((await languages.SetLocaleAsync("{\"locale\":\"en-GB\"}", CancellationToken.None)).Ok);
	}

	[Fact]
	public async Task FailedSettingsWriteDoesNotActivatePreparedDictionary() {
		using var settings = CoreSettings.CreateStore(SettingsPath, enableWatcher: false);
		using var handler = new DictionaryServer { OnRequest = () => { if (!Directory.Exists(SettingsPath)) { File.Delete(SettingsPath); Directory.CreateDirectory(SettingsPath); } } };
		using var http = new HttpClient(handler);
		using var languages = new SpellLanguages(settings, http);
		var result = await languages.SetLocaleAsync("{\"locale\":\"en-GB\"}", CancellationToken.None);
		Assert.False(result.Ok);
		Assert.True(languages.Current.Check("color"));
		Assert.True(Directory.Exists(SettingsPath));
	}

	[Fact]
	public async Task InvalidLocaleCannotEscapeCacheAndMissingSelectionNeverUsesEnglish() {
		using var settings = CoreSettings.CreateStore(SettingsPath, enableWatcher: false);
		using var handler = new DictionaryServer();
		using var http = new HttpClient(handler);
		using var languages = new SpellLanguages(settings, http);
		Assert.False((await languages.SetLocaleAsync("{\"locale\":\"../outside\"}", CancellationToken.None)).Ok);
		Assert.Empty(handler.Requests);
		settings.Set(EditorSettings.SpellCheckLocale, JsonSerializer.SerializeToElement("fr"));
		Assert.Contains("Set Spelling Locale", Assert.Throws<InvalidOperationException>(() => languages.Current).Message);
	}

	[Fact]
	public async Task CatalogueUsesUpstreamCodesAndSuggestsCanonicalUnicode() {
		using var settings = CoreSettings.CreateStore(SettingsPath, enableWatcher: false);
		using var handler = new DictionaryServer();
		using var http = new HttpClient(handler);
		using var languages = new SpellLanguages(settings, http);
		var result = await languages.SetLocaleAsync(null, CancellationToken.None);
		using var data = JsonDocument.Parse(result.DataJson!);
		Assert.Equal(["en-US", "fr"], data.RootElement.GetProperty("locales").EnumerateArray().Select(value => value.GetString()));
		Assert.True((await languages.SetLocaleAsync("{\"locale\":\"fr\"}", CancellationToken.None)).Ok);
		Assert.Empty(SpellChecker.Check(languages.Current, [new(1, 0, "e\u0301cole")], new HashSet<string>(), new HashSet<string>(), CancellationToken.None));
	}

	public void Dispose() => _directory.Dispose();

	private sealed class DictionaryServer : HttpMessageHandler {
		public List<string> Requests { get; } = [];
		public Action OnRequest { get; set; } = () => { };
		public string FailedFile { get; init; } = "";
		public bool Empty { get; init; }
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
			OnRequest();
			string path = request.RequestUri!.AbsolutePath;
			Requests.Add(path);
			string file = Path.GetFileName(path);
			bool failed = file == FailedFile;
			string content = file switch {
				"index.aff" => "SET UTF-8\nTRY abcdefghijklmnopqrstuvwxyz\n",
				"index.dic" => failed && Empty ? "0\n" : "2\ncolour\nécole\n",
				"license" => "test license",
				_ => "[{\"name\":\"en\"},{\"name\":\"fr\"}]",
			};
			return Task.FromResult(new HttpResponseMessage(failed && !Empty ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new StringContent(content) });
		}
	}
}
