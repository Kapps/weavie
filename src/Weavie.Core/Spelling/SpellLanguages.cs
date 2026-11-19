using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using WeCantSpell.Hunspell;

namespace Weavie.Core.Spelling;

/// <summary>Versioned Hunspell dictionaries, downloaded under a cache root the first time a locale is asked for.</summary>
public sealed partial class SpellLanguages(string cacheRoot, HttpClient http) : IDisposable {
	/// <summary>What a locale code looks like, for the surfaces that reject one.</summary>
	public const string LocaleCodeHint = "Use a locale code such as en-US, en-GB, fr, or de. Run Set Spelling Locale without arguments to list available codes.";

	private const string Revision = "8cfea406b505e4d7df52d5a19bce525df98c54ab";
	private readonly string _cache = Path.Combine(cacheRoot, "dictionaries", Revision);
	private readonly CancellationTokenSource _lifetime = new();
	private readonly Lock _gate = new();
	private readonly Dictionary<string, Task<SpellVocabulary>> _installs = new(StringComparer.Ordinal);

	/// <summary>Whether <paramref name="locale"/> has the shape of a dictionary locale code.</summary>
	public static bool IsLocaleCode(string locale) => LocaleCode().IsMatch(locale);

	/// <summary>
	/// The dictionary for <paramref name="locale"/>, installing it when this machine doesn't have it. Callers for
	/// one locale share one install, which outlives any single caller's cancellation.
	/// </summary>
	public async Task<SpellVocabulary> ForAsync(string locale, CancellationToken ct) {
		if (locale == "en") return SpellVocabulary.English.Value;
		if (!IsLocaleCode(locale)) throw new ArgumentException(LocaleCodeHint);
		string path = Path.Combine(_cache, locale);
		Task<SpellVocabulary> install;
		lock (_gate) {
			if (!_installs.TryGetValue(locale, out install!)) {
				var lifetime = _lifetime.Token;
				install = Task.Run(() => Directory.Exists(path) ? Task.FromResult(Load(path, locale)) : DownloadAsync(locale, path, lifetime), lifetime);
				_installs[locale] = install;
			}
		}
		try {
			return await install.WaitAsync(ct).ConfigureAwait(false);
		} catch when (install.IsFaulted || install.IsCanceled) {
			lock (_gate) {
				if (_installs.GetValueOrDefault(locale) == install) _installs.Remove(locale);
			}
			throw;
		}
	}

	/// <summary>Handles the agent-callable command against the device's <paramref name="settings"/>; no argument returns the upstream catalogue.</summary>
	public async Task<CommandResult> SetLocaleAsync(SettingsStore settings, string? argsJson, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(settings);
		try {
			using var args = JsonDocument.Parse(argsJson ?? "{}");
			if (!args.RootElement.TryGetProperty("locale", out var value)) {
				using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/wooorm/dictionaries/contents/dictionaries?ref={Revision}");
				request.Headers.UserAgent.ParseAdd("Weavie");
				using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
				response.EnsureSuccessStatusCode();
				var entries = await response.Content.ReadFromJsonAsync(CoreJson.Default.JsonElement, ct).ConfigureAwait(false);
				string[] locales = [.. entries.EnumerateArray().Select(entry => entry.GetProperty("name").GetString()!).Select(name => name == "en" ? "en-US" : name).Prepend("en").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
				return CommandResult.Success($"Current spelling locale: {settings.RequireString(EditorSettings.SpellCheckLocale)}. Ask the agent to select a locale.", JsonSerializer.Serialize(new SpellLocales(locales), PageJson.Default.SpellLocales));
			}
			string locale = value.GetString() ?? throw new ArgumentException("Provide a locale code.");
			// Installing here proves the dictionary exists before the locale is saved.
			await ForAsync(locale, ct).ConfigureAwait(false);
			var result = settings.Set(EditorSettings.SpellCheckLocale, JsonSerializer.SerializeToElement(locale, CoreJson.Default.String));
			return CommandResult.Success(result.ShadowedByEnv is { } variable
				? $"Saved spelling locale '{locale}', but {variable} overrides it. Effective locale: {settings.RequireString(EditorSettings.SpellCheckLocale)}."
				: $"Spelling locale set to {locale}.");
		} catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException or InvalidDataException or SettingValidationException or SettingsFileMalformedException) {
			return CommandResult.Failure(ex.Message);
		}
	}

	private async Task<SpellVocabulary> DownloadAsync(string locale, string path, CancellationToken ct) {
		Directory.CreateDirectory(_cache);
		string staging = Path.Combine(_cache, Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(staging);
		try {
			string sourceLocale = locale == "en-US" ? "en" : locale;
			foreach (string file in new[] { "index.aff", "index.dic", "license" }) {
				using var response = await http.GetAsync($"https://raw.githubusercontent.com/wooorm/dictionaries/{Revision}/dictionaries/{sourceLocale}/{file}", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
				if (response.StatusCode == System.Net.HttpStatusCode.NotFound) throw new ArgumentException($"No dictionary for '{locale}'. Run Set Spelling Locale without arguments to list available codes.");
				response.EnsureSuccessStatusCode();
				await using var output = File.Create(Path.Combine(staging, file));
				await response.Content.CopyToAsync(output, ct).ConfigureAwait(false);
			}
			var words = Load(staging, locale);
			ct.ThrowIfCancellationRequested();
			try { Directory.Move(staging, path); } catch (IOException) when (Directory.Exists(path)) { return Load(path, locale); }
			return words;
		} finally {
			if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
		}
	}

	private static SpellVocabulary Load(string path, string locale) {
		var words = WordList.CreateFromFiles(Path.Combine(path, "index.dic"), Path.Combine(path, "index.aff"));
		if (words.RootCount == 0) throw new InvalidDataException("The downloaded spelling dictionary contains no words.");
		return new(words, english: locale.StartsWith("en-", StringComparison.Ordinal));
	}

	[GeneratedRegex(@"\A[a-z]{2,3}(?:-[A-Za-z0-9]+)*\z", RegexOptions.NonBacktracking)]
	private static partial Regex LocaleCode();

	/// <summary>Stops installs still downloading.</summary>
	public void Dispose() {
		_lifetime.Cancel();
		_lifetime.Dispose();
	}
}

internal sealed record SpellLocales(IReadOnlyList<string> Locales);
