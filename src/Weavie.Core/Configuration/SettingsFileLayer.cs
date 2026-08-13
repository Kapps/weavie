using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Syntax;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Configuration;

/// <summary>
/// One TOML settings file — its parsed document, derived model, and malformed state — with the
/// comment-preserving read/write/remove operations <see cref="SettingsStore"/> composes into layers
/// (the shared user file plus one per workspace). Keeps its last-good document across a malformed
/// reload so live resolution stays stable. Not thread-safe: <see cref="SettingsStore"/> serializes
/// every call under its own lock.
/// </summary>
internal sealed class SettingsFileLayer : IDisposable {
	private static readonly LocalFileSystem FileSystem = new();
	private readonly ReloadingFile<SettingsDocument> _file;

	/// <summary>Creates and loads a layer over <paramref name="filePath"/>.</summary>
	public SettingsFileLayer(string filePath, Lock gate) {
		_file = new ReloadingFile<SettingsDocument>(
			filePath,
			gate,
			new SettingsDocument(new DocumentSyntax(), []),
			Load,
			watch: false);
	}

	/// <summary>The TOML file backing this layer.</summary>
	public string FilePath => _file.Path;

	/// <summary>Whether the on-disk file currently has TOML parse errors (writes are refused while true).</summary>
	public bool Malformed => _file.Error is not null;

	internal event Action<FileReload<SettingsDocument>>? Reloaded {
		add => _file.Reloaded += value;
		remove => _file.Reloaded -= value;
	}

	internal void Watch() => _file.Watch();

	/// <summary>Reads the raw TOML value for a dotted <paramref name="key"/> (root dotted key or nested table).</summary>
	public bool TryGetValue(string key, out object? value) {
		value = null;
		string[] parts = key.Split('.');
		var table = _file.Value.Model;
		for (int i = 0; i < parts.Length; i++) {
			if (!table.TryGetValue(parts[i], out object? current)) {
				return false;
			}

			if (i == parts.Length - 1) {
				value = current;
				return true;
			}

			if (current is TomlTable next) {
				table = next;
			} else {
				return false; // a leaf where a subtable was expected
			}
		}

		return false;
	}

	/// <summary>Sets <paramref name="definition"/>'s key = <paramref name="coerced"/> in the document, updating every
	/// existing entry in place or appending a new key self-documented with the definition's description. Rebuilds the model.</summary>
	public void SetValue(SettingDefinition definition, object? coerced) {
		var document = _file.Value;
		var existing = FindEntries(definition.Key);
		if (existing.Count > 0) {
			// Update every form the key appears in — root dotted and nested under a hand-written [table] header —
			// so a table entry can't keep shadowing the write. A fresh value node per entry: nodes are single-parent.
			foreach (var (_, node) in existing) {
				node.Value = BuildValueSyntax(definition, coerced);
			}
		} else {
			var keyValue = new KeyValueSyntax(BuildKeySyntax(definition.Key), BuildValueSyntax(definition, coerced)) {
				// Self-document a newly written key with its description; existing lines are never touched.
				LeadingTrivia = [
					new SyntaxTrivia(TokenKind.Comment, "# " + definition.Description),
					new SyntaxTrivia(TokenKind.NewLine, "\n"),
				],
			};
			document.Syntax.KeyValues.Add(keyValue);
		}

		document.Model = document.Syntax.ToModel();
	}

	// Removes every user entry for `key` — the root-level dotted form and entries nested under a
	// hand-edited [table] header. An emptied table header is left in place: pruning it risks dropping comments.
	public bool RemoveKey(string key) {
		var document = _file.Value;
		var matches = FindEntries(key);
		foreach (var (owner, node) in matches) {
			owner.RemoveChild(node);
		}

		if (matches.Count > 0) {
			document.Model = document.Syntax.ToModel();
		}

		return matches.Count > 0;
	}

	// Every entry for a dotted key, in both forms it can appear in — a root-level dotted key and an entry
	// nested under a [table] header — paired with the list that owns it (for removal).
	private List<(SyntaxList<KeyValueSyntax> Owner, KeyValueSyntax Node)> FindEntries(string key) {
		var documentSyntax = _file.Value.Syntax;
		var matches = new List<(SyntaxList<KeyValueSyntax>, KeyValueSyntax)>();
		foreach (var keyValue in documentSyntax.KeyValues) {
			if (keyValue.Key is { } keySyntax && string.Equals(DottedKeyName(keySyntax), key, StringComparison.Ordinal)) {
				matches.Add((documentSyntax.KeyValues, keyValue));
			}
		}

		foreach (var table in documentSyntax.Tables) {
			if (table.Name is not { } tableName) {
				continue;
			}

			string prefix = DottedKeyName(tableName);
			foreach (var item in table.Items) {
				if (item.Key is { } keySyntax
					&& string.Equals($"{prefix}.{DottedKeyName(keySyntax)}", key, StringComparison.Ordinal)) {
					matches.Add((table.Items, item));
				}
			}
		}

		return matches;
	}

	/// <summary>Writes the document to disk atomically (temp file + replace), creating the directory if needed.</summary>
	public void SaveAtomic() => FileSystem.WriteAllTextAtomic(FilePath, _file.Value.Syntax.ToString());

	public void Dispose() => _file.Dispose();

	private static SettingsDocument Load(string path) {
		string text = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
		var parsed = Toml.Parse(text, path);
		if (parsed.HasErrors) {
			throw new InvalidDataException(string.Join(Environment.NewLine, parsed.Diagnostics));
		}
		return new SettingsDocument(parsed, parsed.ToModel());
	}

	private static ValueSyntax BuildValueSyntax(SettingDefinition definition, object? coerced) =>
		definition.Kind switch {
			SettingKind.Bool => new BooleanValueSyntax((bool)coerced!),
			SettingKind.Int => new IntegerValueSyntax((long)coerced!),
			SettingKind.Path => MakeStringSyntax((string)coerced!, preferLiteral: true),
			_ => MakeStringSyntax((string)coerced!, preferLiteral: ((string)coerced!).Contains('\\', StringComparison.Ordinal)),
		};

	// Emit a single-quoted literal string (no escaping — ideal for Windows paths) when the value has
	// no character that a literal string can't hold; otherwise a normal escaped basic string.
	private static StringValueSyntax MakeStringSyntax(string value, bool preferLiteral) {
		if (preferLiteral
			&& !value.Contains('\'', StringComparison.Ordinal)
			&& !value.Contains('\n', StringComparison.Ordinal)
			&& !value.Contains('\r', StringComparison.Ordinal)) {
			return new StringValueSyntax(value) {
				Token = new SyntaxToken(TokenKind.StringLiteral, "'" + value + "'"),
			};
		}

		return new StringValueSyntax(value);
	}

	private static KeySyntax BuildKeySyntax(string key) {
		string[] parts = key.Split('.');
		// Tomlyn's KeySyntax only takes the first segment (+ optionally one dot key) via its
		// constructors; deeper dotted keys (e.g. editor.font.size) are built by appending DotKeys.
		var syntax = new KeySyntax(parts[0]);
		for (int i = 1; i < parts.Length; i++) {
			syntax.DotKeys.Add(new DottedKeyItemSyntax(parts[i]));
		}

		return syntax;
	}

	private static string DottedKeyName(KeySyntax key) {
		var parts = new List<string> { KeyPartName(key.Key) };
		foreach (var dot in key.DotKeys) {
			parts.Add(KeyPartName(dot.Key));
		}

		return string.Join('.', parts);
	}

	private static string KeyPartName(BareKeyOrStringValueSyntax? part) => part switch {
		BareKeySyntax bare => bare.Key?.Text?.Trim() ?? string.Empty,
		StringValueSyntax str => str.Value ?? string.Empty,
		_ => part?.ToString()?.Trim() ?? string.Empty,
	};

	internal sealed class SettingsDocument {
		internal SettingsDocument(DocumentSyntax syntax, TomlTable model) {
			Syntax = syntax;
			Model = model;
		}

		internal DocumentSyntax Syntax { get; }

		internal TomlTable Model { get; set; }
	}
}
