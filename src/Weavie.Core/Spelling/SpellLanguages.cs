using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using WeCantSpell.Hunspell;

namespace Weavie.Core.Spelling;

/// <summary>Installs versioned Hunspell dictionaries before persisting the selected backend locale.</summary>
public sealed partial class SpellLanguages(SettingsStore settings, HttpClient http) : IDisposable {
	private const string Revision = "8cfea406b505e4d7df52d5a19bce525df98c54ab";
	private readonly string _cache = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settings.FilePath))!, "dictionaries", Revision);
	private readonly SemaphoreSlim _install = new(1);
	private readonly Lock _gate = new();
	private (string Locale, WordList Words)? _loaded;

	/// <summary>Resolves one immutable dictionary for the duration of a check or suggestion request.</summary>
	public WordList Current {
		get {
			string locale = settings.RequireString(EditorSettings.SpellCheckLocale);
			if (locale == "en-US") return SpellChecker.English.Value;
			lock (_gate) {
				if (_loaded is { } cached && cached.Locale == locale) return cached.Words;
				string path = LocalePath(locale);
				if (!Directory.Exists(path)) throw new InvalidOperationException($"Spelling locale '{locale}' is not installed. Run Set Spelling Locale to download it.");
				var words = Load(path);
				_loaded = (locale, words);
				return words;
			}
		}
	}

	/// <summary>Handles the agent-callable command; no argument returns the upstream catalogue.</summary>
	public async Task<CommandResult> SetLocaleAsync(string? argsJson, CancellationToken ct) {
		await _install.WaitAsync(ct).ConfigureAwait(false);
		try {
			using var args = JsonDocument.Parse(argsJson ?? "{}");
			if (!args.RootElement.TryGetProperty("locale", out var value)) {
				using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/wooorm/dictionaries/contents/dictionaries?ref={Revision}");
				request.Headers.UserAgent.ParseAdd("Weavie");
				using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
				response.EnsureSuccessStatusCode();
				var entries = await response.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
				string[] locales = [.. entries.EnumerateArray().Select(entry => entry.GetProperty("name").GetString()!).Select(name => name == "en" ? "en-US" : name)];
				return CommandResult.Success($"Current spelling locale: {settings.RequireString(EditorSettings.SpellCheckLocale)}. Ask the agent to select a locale.", JsonSerializer.Serialize(new { locales }));
			}
			string locale = value.GetString() ?? throw new ArgumentException("Provide a locale code.");
			string path = LocalePath(locale);
			if (locale != "en-US") {
				var words = Directory.Exists(path) ? Load(path) : await DownloadAsync(locale, path, ct).ConfigureAwait(false);
				lock (_gate) _loaded = (locale, words);
			}
			ct.ThrowIfCancellationRequested();
			var result = settings.Set(EditorSettings.SpellCheckLocale, JsonSerializer.SerializeToElement(locale));
			return CommandResult.Success(result.ShadowedByEnv is { } variable
				? $"Saved spelling locale '{locale}', but {variable} overrides it. Effective locale: {settings.RequireString(EditorSettings.SpellCheckLocale)}."
				: $"Spelling locale set to {locale}.");
		} catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException or InvalidDataException or SettingValidationException or SettingsFileMalformedException) {
			return CommandResult.Failure(ex.Message);
		} finally {
			_install.Release();
		}
	}

	private string LocalePath(string locale) {
		if (!LocaleCode().IsMatch(locale)) throw new ArgumentException("Use a locale code such as en-US, en-GB, fr, or de. Run Set Spelling Locale without arguments to list available codes.");
		return Path.Combine(_cache, locale);
	}

	private async Task<WordList> DownloadAsync(string locale, string path, CancellationToken ct) {
		Directory.CreateDirectory(_cache);
		string staging = Path.Combine(_cache, Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(staging);
		try {
			foreach (string file in new[] { "index.aff", "index.dic", "license" }) {
				using var response = await http.GetAsync($"https://raw.githubusercontent.com/wooorm/dictionaries/{Revision}/dictionaries/{locale}/{file}", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
				if (response.StatusCode == System.Net.HttpStatusCode.NotFound) throw new ArgumentException($"No dictionary for '{locale}'. Run Set Spelling Locale without arguments to list available codes.");
				response.EnsureSuccessStatusCode();
				await using var output = File.Create(Path.Combine(staging, file));
				await response.Content.CopyToAsync(output, ct).ConfigureAwait(false);
			}
			var words = Load(staging);
			ct.ThrowIfCancellationRequested();
			try { Directory.Move(staging, path); } catch (IOException) when (Directory.Exists(path)) { return Load(path); }
			return words;
		} finally {
			if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
		}
	}

	private static WordList Load(string path) {
		var words = WordList.CreateFromFiles(Path.Combine(path, "index.dic"), Path.Combine(path, "index.aff"));
		if (words.RootCount == 0) throw new InvalidDataException("The downloaded spelling dictionary contains no words.");
		return words;
	}

	[GeneratedRegex(@"\A[a-z]{2,3}(?:-[A-Za-z0-9]+)*\z", RegexOptions.NonBacktracking)]
	private static partial Regex LocaleCode();

	/// <summary>Releases command serialization resources after the host's requests have drained.</summary>
	public void Dispose() => _install.Dispose();
}
