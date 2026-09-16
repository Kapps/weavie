using System.Text.Json;

namespace Weavie.Core.Theming;

public sealed partial class OpenVsxThemeInstaller {
	/// <summary>Searches a page of color-theme extensions in the registry.</summary>
	public async Task<JsonElement> SearchAsync(string query, int offset, CancellationToken ct) {
		ArgumentOutOfRangeException.ThrowIfNegative(offset);
		string json = await _http.GetStringAsync(
			$"{_registry}/api/-/search?category=Themes&size=20&offset={offset}&query={Uri.EscapeDataString(query)}", ct).ConfigureAwait(false);
		using var document = JsonDocument.Parse(json);
		return document.RootElement.Clone();
	}

	/// <summary>Downloads themes for preview without changing the installed catalog.</summary>
	public async Task<IReadOnlyList<ThemePreview>> PreviewAsync(
		string ns, string name, string version, ThemeOverridesStore overrides, CancellationToken ct) {
		var (vsix, resolvedVersion) = await DownloadAsync(ns, name, version, ct).ConfigureAwait(false);
		string directory = Path.Combine(Path.GetTempPath(), $"weavie-theme-preview-{Guid.NewGuid():N}");
		try {
			var themes = await ExtractAsync(ns, name, resolvedVersion, vsix, directory, ct).ConfigureAwait(false);
			if (themes.Count == 0) {
				throw new InvalidOperationException("This extension contributes no color themes.");
			}
			return themes.Select(theme => new ThemePreview(
				ThemeCatalog.Describe(theme), ThemeJson.BuildSlot(theme.Id, ThemeJson.LoadInstalled(theme), overrides))).ToArray();
		} finally {
			if (Directory.Exists(directory)) {
				Directory.Delete(directory, recursive: true);
			}
		}
	}

	private async Task<(byte[] Vsix, string Version)> DownloadAsync(
		string ns, string name, string? version, CancellationToken ct) {
		ValidateCoordinate(ns);
		ValidateCoordinate(name);
		if (version is not null) ValidateCoordinate(version);
		string url = $"{_registry}/api/{Uri.EscapeDataString(ns)}/{Uri.EscapeDataString(name)}";
		if (version is not null) url += $"/{Uri.EscapeDataString(version)}";
		string metadata = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
		var (downloadUrl, resolvedVersion) = ParseMetadata(metadata);
		if (downloadUrl is null || resolvedVersion is null) {
			throw new InvalidOperationException($"Open VSX metadata for {ns}.{name} has no .vsix download.");
		}
		ValidateCoordinate(resolvedVersion);
		if (!IsTrustedDownloadUrl(downloadUrl)) {
			throw new InvalidOperationException($"Open VSX returned an untrusted .vsix URL for {ns}.{name}.");
		}
		return (await _http.GetByteArrayAsync(downloadUrl, ct).ConfigureAwait(false), resolvedVersion);
	}

	private static void ValidateCoordinate(string value) {
		if (string.IsNullOrWhiteSpace(value) || value is "." or ".."
			|| value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_' and not '+')) {
			throw new InvalidOperationException("Invalid extension publisher, name, or version.");
		}
	}
}
