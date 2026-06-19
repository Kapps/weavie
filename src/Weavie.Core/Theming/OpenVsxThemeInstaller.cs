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
		return await ExtractAndIndexAsync(ns, name, resolvedVersion, vsix, ct).ConfigureAwait(false);
	}

	/// <summary>
	/// Installs a VS Code color theme from a local <c>.vsix</c> file — the same ZIP an Open VSX download is —
	/// returning the themes it contributes. The extension's identity (publisher / name / version) is read
	/// from the vsix's own manifest, so a re-install (from file or Open VSX) replaces the prior copy of that
	/// extension and the resulting theme ids match either install path.
	/// </summary>
	/// <param name="vsixPath">Absolute path to a <c>.vsix</c> file.</param>
	/// <param name="ct">Cancellation token.</param>
	public async Task<IReadOnlyList<InstalledTheme>> InstallFromVsixAsync(string vsixPath, CancellationToken ct) {
		ArgumentException.ThrowIfNullOrEmpty(vsixPath);
		if (!File.Exists(vsixPath)) {
			throw new FileNotFoundException($"No .vsix file at '{vsixPath}'.", vsixPath);
		}

		byte[] vsix = await File.ReadAllBytesAsync(vsixPath, ct).ConfigureAwait(false);
		var (publisher, name, version) = ParseExtensionIdentity(ReadExtensionManifest(vsix));
		return await ExtractAndIndexAsync(publisher, name, version, vsix, ct).ConfigureAwait(false);
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

	/// <summary>
	/// Reads an extension's identity (<c>publisher</c>, <c>name</c>, <c>version</c>) from its
	/// <c>package.json</c> — the coordinates a local-file install needs (an Open VSX install gets them from
	/// the registry instead). Throws <see cref="InvalidOperationException"/> if any are missing. Pure.
	/// </summary>
	/// <param name="packageJson">The extension's <c>package.json</c> contents.</param>
	public static (string Publisher, string Name, string Version) ParseExtensionIdentity(string packageJson) {
		using var doc = JsonDocument.Parse(packageJson);
		var root = doc.RootElement;
		string? publisher = root.TryGetProperty("publisher", out var pub) ? pub.GetString() : null;
		string? name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
		string? version = root.TryGetProperty("version", out var v) ? v.GetString() : null;
		if (string.IsNullOrEmpty(publisher) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(version)) {
			throw new InvalidOperationException(
				"The .vsix manifest is missing a publisher, name, or version; it is not a valid VS Code extension.");
		}

		return (publisher, name, version);
	}

	// Shared install tail for both the Open VSX download and a local-file install: extract the .vsix into the
	// themes root under a per-extension directory (replacing any prior copy), read the manifest's contributed
	// themes, record them in the index, and return them.
	private static async Task<IReadOnlyList<InstalledTheme>> ExtractAndIndexAsync(
		string ns, string name, string version, byte[] vsix, CancellationToken ct) {
		string extractDir = Path.Combine(WeaviePaths.Themes, $"{ns}.{name}-{version}");
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
			.Select(c => new InstalledTheme($"{ns}.{name}/{c.Label}", c.Label, c.UiTheme, ns, name, version, c.Path))
			.ToList();
		MergeIntoIndex(ns, name, installed);
		return installed;
	}

	// Reads the extension's package.json straight out of the .vsix ZIP (entries use forward slashes), so a
	// local-file install can learn the extension's coordinates before choosing an extract directory.
	private static string ReadExtensionManifest(byte[] vsix) {
		using var archive = new ZipArchive(new MemoryStream(vsix), ZipArchiveMode.Read);
		var entry = archive.GetEntry("extension/package.json")
			?? throw new InvalidOperationException("Not a VS Code extension: the .vsix has no extension/package.json.");
		using var reader = new StreamReader(entry.Open());
		return reader.ReadToEnd();
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
