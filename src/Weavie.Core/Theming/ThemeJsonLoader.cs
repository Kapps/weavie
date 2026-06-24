using System.Text.Json.Nodes;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Theming;

/// <summary>
/// Loads a VS Code color theme's JSON from disk into one self-contained object, merging away its
/// <c>include</c> chain (a theme may extend another by relative path) since the web serves a theme as a single
/// file. Merge semantics live in <see cref="MergeOnto"/>.
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
		string full = Path.GetFullPath(themePath);
		// Confine the include chain to the theme's own directory tree, so an installed (untrusted) theme's
		// `include` can't escape to read an arbitrary file (e.g. ../../../etc/passwd).
		string root = Path.GetDirectoryName(full) ?? full;
		return LoadMergedInner(full, root, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
	}

	private JsonObject LoadMergedInner(string path, string root, HashSet<string> visited, int depth) {
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
			if (!BufferStore.IsWithinWorkspace(root, includedPath)) {
				throw new InvalidOperationException($"theme include '{relative}' escapes the theme directory");
			}

			var baseTheme = LoadMergedInner(includedPath, root, visited, depth + 1);
			node.Remove("include");
			MergeOnto(baseTheme, node);
			return baseTheme;
		}

		return node;
	}

	// VS Code include semantics: `colors`/`semanticTokenColors` shallow-merge (overlay wins per key),
	// `tokenColors` append (base first, overlay last), every other key is replaced by the overlay's value.
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
