using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Theming;

/// <summary>
/// Per-theme color overrides, persisted to <c>~/.weavie/theme-overrides.json</c> as its own document,
/// separate from <c>settings.toml</c>. Keyed by theme id, so overrides stay with their theme across
/// switches; each theme's ops are an ordered list applied at resolve time, so undo is "pop the last op".
/// Persistence mirrors <see cref="Weavie.Core.Workspaces.RecentWorkspaces"/>: atomic writes, and a
/// malformed file is backed up to <c>theme-overrides.json.bad</c> and reset rather than thrown.
/// </summary>
public sealed class ThemeOverridesStore {
	private static readonly JsonSerializerOptions JsonOptions = new() {
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	private readonly IFileSystem _fileSystem;
	private readonly Lock _gate = new();
	private readonly Dictionary<string, List<ThemeOverrideOp>> _overrides;

	/// <summary>Creates the store over <paramref name="path"/> (default <c>~/.weavie/theme-overrides.json</c>) and loads it.</summary>
	public ThemeOverridesStore(IFileSystem fileSystem, string? path) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		_fileSystem = fileSystem;
		FilePath = path ?? WeaviePaths.ThemeOverridesFile;
		lock (_gate) {
			_overrides = LoadLocked();
		}
	}

	/// <summary>Raised (off the UI thread) after a theme's overrides change, carrying that theme's id.</summary>
	public event Action<string>? Changed;

	/// <summary>Diagnostic log line — read failures, malformed-file resets, persist failures.</summary>
	public event Action<string>? Log;

	/// <summary>The overrides file backing this store.</summary>
	public string FilePath { get; }

	/// <summary>The override ops for <paramref name="themeId"/>, in order (empty if none). Snapshot copy.</summary>
	public IReadOnlyList<ThemeOverrideOp> Get(string themeId) {
		ArgumentException.ThrowIfNullOrEmpty(themeId);
		lock (_gate) {
			return _overrides.TryGetValue(themeId, out var ops) ? [.. ops] : [];
		}
	}

	/// <summary>Appends <paramref name="op"/> to <paramref name="themeId"/>'s ordered op list.</summary>
	public void Append(string themeId, ThemeOverrideOp op) {
		ArgumentException.ThrowIfNullOrEmpty(themeId);
		ArgumentNullException.ThrowIfNull(op);
		lock (_gate) {
			if (!_overrides.TryGetValue(themeId, out var ops)) {
				ops = [];
				_overrides[themeId] = ops;
			}

			ops.Add(op);
			PersistLocked();
		}

		Changed?.Invoke(themeId);
	}

	/// <summary>Replaces <paramref name="themeId"/>'s ops wholesale (e.g. after removing one by key); empty clears it.</summary>
	public void SetOps(string themeId, IReadOnlyList<ThemeOverrideOp> ops) {
		ArgumentException.ThrowIfNullOrEmpty(themeId);
		ArgumentNullException.ThrowIfNull(ops);
		lock (_gate) {
			if (ops.Count == 0) {
				_overrides.Remove(themeId);
			} else {
				_overrides[themeId] = [.. ops];
			}

			PersistLocked();
		}

		Changed?.Invoke(themeId);
	}

	/// <summary>Removes the last op for <paramref name="themeId"/> (the spec's undo); returns false if there were none.</summary>
	public bool UndoLast(string themeId) {
		ArgumentException.ThrowIfNullOrEmpty(themeId);
		bool removed;
		lock (_gate) {
			if (_overrides.TryGetValue(themeId, out var ops) && ops.Count > 0) {
				ops.RemoveAt(ops.Count - 1);
				if (ops.Count == 0) {
					_overrides.Remove(themeId);
				}

				removed = true;
				PersistLocked();
			} else {
				removed = false;
			}
		}

		if (removed) {
			Changed?.Invoke(themeId);
		}

		return removed;
	}

	/// <summary>Clears all overrides for <paramref name="themeId"/> (the spec's reset); returns false if there were none.</summary>
	public bool Clear(string themeId) {
		ArgumentException.ThrowIfNullOrEmpty(themeId);
		bool removed;
		lock (_gate) {
			removed = _overrides.Remove(themeId);
			if (removed) {
				PersistLocked();
			}
		}

		if (removed) {
			Changed?.Invoke(themeId);
		}

		return removed;
	}

	private Dictionary<string, List<ThemeOverrideOp>> LoadLocked() {
		if (!_fileSystem.FileExists(FilePath)) {
			return [];
		}

		string text;
		try {
			text = _fileSystem.ReadAllText(FilePath);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[theme-overrides] could not read {FilePath}: {ex.Message}; starting empty");
			return [];
		}

		try {
			var document = JsonSerializer.Deserialize<OverridesDocument>(text, JsonOptions);
			if (document?.Overrides is not { } map) {
				return [];
			}

			var result = new Dictionary<string, List<ThemeOverrideOp>>();
			foreach (var (themeId, ops) in map) {
				if (!string.IsNullOrWhiteSpace(themeId) && ops is { Count: > 0 }) {
					result[themeId] = [.. ops];
				}
			}

			return result;
		} catch (JsonException ex) {
			Log?.Invoke($"[theme-overrides] {FilePath} is malformed ({ex.Message}); backing up to theme-overrides.json.bad and resetting");
			BackupBadFileLocked(text);
			return [];
		}
	}

	private void BackupBadFileLocked(string text) {
		try {
			_fileSystem.WriteAllText(FilePath + ".bad", text);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[theme-overrides] could not back up malformed overrides: {ex.Message}");
		}
	}

	private void PersistLocked() {
		try {
			var document = new OverridesDocument {
				Version = 1,
				Overrides = _overrides.ToDictionary(kv => kv.Key, kv => kv.Value),
			};
			_fileSystem.WriteAllTextAtomic(FilePath, JsonSerializer.Serialize(document, JsonOptions));
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[theme-overrides] could not persist overrides: {ex.Message}");
		}
	}

	private sealed class OverridesDocument {
		[JsonPropertyName("version")]
		public int Version { get; set; }

		[JsonPropertyName("overrides")]
		public Dictionary<string, List<ThemeOverrideOp>> Overrides { get; set; } = [];
	}
}
