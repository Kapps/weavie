using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Weavie.Core.Theming;

/// <summary>
/// Installs VS Code color themes from the <see href="https://open-vsx.org">Open VSX</see> registry
/// into the Weavie themes root (<see cref="WeaviePaths.Themes"/>), per spec §8/§13. A theme ships as a
/// <c>.vsix</c> (a ZIP) — pure declarative data (theme JSON + a <c>package.json</c> manifest), no
/// extension host. The host owns this (network + filesystem); we download, unzip, read the manifest's
/// <c>contributes.themes[]</c>, and record selectable themes in <c>~/.weavie/themes/index.json</c>.
/// The raw theme JSON is kept as the lossless source of truth; conversion happens at load (web side).
/// </summary>
public sealed class OpenVsxThemeInstaller {
	private const string DefaultRegistry = "https://open-vsx.org";
	private const string IndexFileName = "index.json";

	private readonly HttpClient _http;
	private readonly string _registry;

	/// <summary>Creates the installer. Pass an <see cref="HttpClient"/> to share/mock; one is created otherwise.</summary>
	/// <param name="http">HTTP client used for registry calls; a default is created if null.</param>
	/// <param name="registry">Registry base URL; defaults to the public Open VSX.</param>
	public OpenVsxThemeInstaller(HttpClient? http = null, string registry = DefaultRegistry) {
		_http = http ?? new HttpClient();
		_registry = registry.TrimEnd('/');
	}

	/// <summary>The path of the themes index file: <c>~/.weavie/themes/index.json</c>.</summary>
	public static string IndexPath => Path.Combine(WeaviePaths.Themes, IndexFileName);

	/// <summary>
	/// Downloads and installs the extension <c>{namespace}.{name}</c> (latest, or a specific
	/// <paramref name="version"/>) from Open VSX, returning the themes it contributes. Re-installing
	/// replaces the prior copy of that extension.
	/// </summary>
	/// <param name="ns">Open VSX namespace (publisher).</param>
	/// <param name="name">Extension name.</param>
	/// <param name="version">Specific version, or null for the latest.</param>
	/// <param name="ct">Cancellation token.</param>
	public async Task<IReadOnlyList<InstalledTheme>> InstallAsync(string ns, string name, string? version, CancellationToken ct) {
		ArgumentException.ThrowIfNullOrEmpty(ns);
		ArgumentException.ThrowIfNullOrEmpty(name);

		string metaUrl = version is null
			? $"{_registry}/api/{ns}/{name}"
			: $"{_registry}/api/{ns}/{name}/{version}";
		string metadata = await _http.GetStringAsync(metaUrl, ct).ConfigureAwait(false);
		var (downloadUrl, resolvedVersion) = ParseMetadata(metadata);
		if (downloadUrl is null || resolvedVersion is null) {
			throw new InvalidOperationException($"Open VSX metadata for {ns}.{name} has no .vsix download.");
		}

		byte[] vsix = await _http.GetByteArrayAsync(downloadUrl, ct).ConfigureAwait(false);
		string extractDir = Path.Combine(WeaviePaths.Themes, $"{ns}.{name}-{resolvedVersion}");
		if (Directory.Exists(extractDir)) {
			Directory.Delete(extractDir, recursive: true);
		}

		Directory.CreateDirectory(extractDir);
		using (var archive = new ZipArchive(new MemoryStream(vsix), ZipArchiveMode.Read)) {
			archive.ExtractToDirectory(extractDir); // .NET guards against zip-slip path traversal
		}

		// .vsix lays the extension out under "extension/"; theme paths in the manifest are relative to it.
		string extensionDir = Path.Combine(extractDir, "extension");
		string manifestPath = Path.Combine(extensionDir, "package.json");
		var contributions = ParseThemeContributions(await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false), extensionDir);

		var installed = contributions
			.Select(c => new InstalledTheme($"{ns}.{name}/{c.Label}", c.Label, c.UiTheme, ns, name, resolvedVersion, c.Path))
			.ToList();
		MergeIntoIndex(ns, name, installed);
		return installed;
	}

	/// <summary>Lists the themes recorded in the index (empty if nothing is installed yet).</summary>
	public static IReadOnlyList<InstalledTheme> ListInstalled() {
		if (!File.Exists(IndexPath)) {
			return [];
		}

		try {
			return JsonSerializer.Deserialize<List<InstalledTheme>>(File.ReadAllText(IndexPath)) ?? [];
		} catch (JsonException) {
			return [];
		}
	}

	/// <summary>
	/// Parses Open VSX extension metadata, returning the <c>.vsix</c> download URL and resolved version
	/// (both null if absent). Pure — no I/O — so it's unit-testable against captured responses.
	/// </summary>
	/// <param name="metadataJson">The JSON body from <c>/api/{namespace}/{name}</c>.</param>
	public static (string? DownloadUrl, string? Version) ParseMetadata(string metadataJson) {
		using var doc = JsonDocument.Parse(metadataJson);
		var root = doc.RootElement;
		string? version = root.TryGetProperty("version", out var v) ? v.GetString() : null;
		string? download = null;
		if (root.TryGetProperty("files", out var files) && files.TryGetProperty("download", out var d)) {
			download = d.GetString();
		}

		return (download, version);
	}

	/// <summary>
	/// Parses a VS Code extension's <c>package.json</c> for its contributed color themes, resolving each
	/// theme's relative <c>path</c> to an absolute path under <paramref name="extensionDir"/>. Pure.
	/// </summary>
	/// <param name="packageJson">The extension's <c>package.json</c> contents.</param>
	/// <param name="extensionDir">The unpacked extension directory (theme paths are relative to it).</param>
	public static IReadOnlyList<ThemeContribution> ParseThemeContributions(string packageJson, string extensionDir) {
		using var doc = JsonDocument.Parse(packageJson);
		if (!doc.RootElement.TryGetProperty("contributes", out var contributes)
			|| !contributes.TryGetProperty("themes", out var themes)
			|| themes.ValueKind != JsonValueKind.Array) {
			return [];
		}

		var result = new List<ThemeContribution>();
		foreach (var theme in themes.EnumerateArray()) {
			string? rawPath = theme.TryGetProperty("path", out var p) ? p.GetString() : null;
			if (string.IsNullOrEmpty(rawPath)) {
				continue;
			}

			string label = (theme.TryGetProperty("label", out var l) ? l.GetString() : null)
				?? (theme.TryGetProperty("id", out var id) ? id.GetString() : null)
				?? Path.GetFileNameWithoutExtension(rawPath);
			string uiTheme = (theme.TryGetProperty("uiTheme", out var u) ? u.GetString() : null) ?? "vs-dark";
			string fullPath = Path.GetFullPath(Path.Combine(extensionDir, rawPath));
			result.Add(new ThemeContribution(label, uiTheme, fullPath));
		}

		return result;
	}

	private static void MergeIntoIndex(string ns, string name, IReadOnlyList<InstalledTheme> installed) {
		var existing = ListInstalled()
			.Where(t => !(string.Equals(t.Namespace, ns, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
			.ToList();
		existing.AddRange(installed);

		Directory.CreateDirectory(WeaviePaths.Themes);
		string json = JsonSerializer.Serialize(existing, new JsonSerializerOptions { WriteIndented = true });
		File.WriteAllText(IndexPath, json, Encoding.UTF8);
	}
}
