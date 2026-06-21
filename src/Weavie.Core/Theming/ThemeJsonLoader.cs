using System.Text.Json.Nodes;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Theming;

/// <summary>
/// Loads a VS Code color theme's JSON from disk into one self-contained object, resolving the theme's
/// <c>include</c> chain (a theme may extend another by relative path). The web serves a theme as one JSON
/// file (a <c>data:</c> URL), so includes are merged away here: the included theme is the base, the
/// including theme's <c>colors</c>/<c>semanticTokenColors</c> override per key, and its <c>tokenColors</c>
/// are appended (base rules first, overriding rules last).
/// </summary>
public sealed class ThemeJsonLoader {
	private const int MaxIncludeDepth = 16;
	private readonly IFileSystem _fileSystem;

	/// <summary>Creates the loader over <paramref name="fileSystem"/> (so include-merging is testable in memory).</summary>
	public ThemeJsonLoader(IFileSystem fileSystem) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		_fileSystem = fileSystem;
	}

	/// <summary>Loads <paramref name="themePath"/>, merging its <c>include</c> chain into one self-contained theme object.</summary>
	public JsonObject LoadMerged(string themePath) {
		ArgumentException.ThrowIfNullOrEmpty(themePath);
		return LoadMergedInner(Path.GetFullPath(themePath), new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
	}

	private JsonObject LoadMergedInner(string path, HashSet<string> visited, int depth) {
		if (depth > MaxIncludeDepth) {
			throw new InvalidOperationException($"theme include chain exceeds {MaxIncludeDepth} levels at {path}");
		}

		if (!visited.Add(path)) {
			throw new InvalidOperationException($"cyclic theme include detected at {path}");
		}

		string text = _fileSystem.ReadAllText(path);
		var node = JsonNode.Parse(text) as JsonObject
			?? throw new InvalidOperationException($"theme {path} is not a JSON object");

		if (node.TryGetPropertyValue("include", out var includeNode)
			&& includeNode is JsonValue includeValue
			&& includeValue.TryGetValue(out string? relative)
			&& !string.IsNullOrEmpty(relative)) {
			string includedPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path) ?? ".", relative));
			var baseTheme = LoadMergedInner(includedPath, visited, depth + 1);
			node.Remove("include");
			MergeOnto(baseTheme, node);
			return baseTheme;
		}

		return node;
	}

	// Applies `overlay` onto `baseObj` with VS Code's include semantics: `colors` + `semanticTokenColors`
	// are shallow object merges (overlay wins per key); `tokenColors` are appended (base first, overlay
	// last); every other key is replaced by the overlay's value.
	private static void MergeOnto(JsonObject baseObj, JsonObject overlay) {
		foreach (var (key, value) in overlay) {
			switch (key) {
				case "colors":
				case "semanticTokenColors":
					if (value is JsonObject overlayMap) {
						if (baseObj[key] is JsonObject baseMap) {
							foreach (var (mapKey, mapValue) in overlayMap) {
								baseMap[mapKey] = mapValue?.DeepClone();
							}
						} else {
							baseObj[key] = overlayMap.DeepClone();
						}
					}

					break;
				case "tokenColors":
					if (value is JsonArray overlayTokens) {
						if (baseObj[key] is JsonArray baseTokens) {
							foreach (var token in overlayTokens) {
								baseTokens.Add(token?.DeepClone());
							}
						} else {
							baseObj[key] = overlayTokens.DeepClone();
						}
					}

					break;
				default:
					baseObj[key] = value?.DeepClone();
					break;
			}
		}
	}
}
