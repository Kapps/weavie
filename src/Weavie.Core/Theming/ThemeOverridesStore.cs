using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Theming;

/// <summary>
/// Per-theme color overrides, persisted to <c>~/.weavie/theme-overrides.json</c> keyed by theme id, so overrides
/// stay with their theme across switches; each theme's ops are an ordered list applied at resolve time (undo =
/// pop the last). Writes are atomic; a malformed file is backed up to <c>theme-overrides.json.bad</c> and reset.
/// </summary>
public sealed class ThemeOverridesStore : JsonDocumentStore {
	private static readonly JsonSerializerOptions JsonOptions = new() {
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	private Dictionary<string, List<ThemeOverrideOp>> _overrides = [];

	/// <summary>Creates the store over <paramref name="path"/> (default <c>~/.weavie/theme-overrides.json</c>) and loads it.</summary>
	/// <param name="fileSystem">The filesystem the overrides persist through.</param>
	/// <param name="path">The backing file, or <c>null</c> for the default.</param>
	public ThemeOverridesStore(IFileSystem fileSystem, string? path)
		: base(fileSystem, path ?? WeaviePaths.ThemeOverridesFile) {
		Load();
	}

	/// <summary>Raised (off the UI thread) after a theme's overrides change, carrying that theme's id.</summary>
	public event Action<string>? Changed;

	/// <summary>The override ops for <paramref name="themeId"/>, in order (empty if none). Snapshot copy.</summary>
	public IReadOnlyList<ThemeOverrideOp> Get(string themeId) {
		ArgumentException.ThrowIfNullOrEmpty(themeId);
		lock (Gate) {
			return _overrides.TryGetValue(themeId, out var ops) ? [.. ops] : [];
		}
	}

	/// <summary>Appends <paramref name="op"/> to <paramref name="themeId"/>'s ordered op list.</summary>
	public void Append(string themeId, ThemeOverrideOp op) {
		ArgumentException.ThrowIfNullOrEmpty(themeId);
		ArgumentNullException.ThrowIfNull(op);
		lock (Gate) {
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
		lock (Gate) {
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
		lock (Gate) {
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
		lock (Gate) {
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

	/// <inheritdoc/>
	protected override void Restore(string? text) {
		var document = text is null ? null : JsonSerializer.Deserialize<OverridesDocument>(text, JsonOptions);
		_overrides = [];
		foreach (var (themeId, ops) in document?.Overrides ?? []) {
			if (!string.IsNullOrWhiteSpace(themeId) && ops is { Count: > 0 }) {
				_overrides[themeId] = [.. ops];
			}
		}
	}

	/// <inheritdoc/>
	protected override string Render() => JsonSerializer.Serialize(
		new OverridesDocument { Version = 1, Overrides = _overrides.ToDictionary(kv => kv.Key, kv => kv.Value) },
		JsonOptions);

	private sealed class OverridesDocument {
		[JsonPropertyName("version")]
		public int Version { get; set; }

		[JsonPropertyName("overrides")]
		public Dictionary<string, List<ThemeOverrideOp>> Overrides { get; set; } = [];
	}
}
