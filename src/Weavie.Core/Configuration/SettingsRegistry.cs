using System.Diagnostics.CodeAnalysis;

namespace Weavie.Core.Configuration;

/// <summary>
/// The catalog of declared settings: register a <see cref="SettingDefinition"/> once and everything
/// downstream (defaults, validation, the env var, the MCP tool surface, NL mapping) is generated from it. Core
/// registers its settings at startup via <see cref="CoreSettings"/>. Registration order is preserved for a
/// stable catalog.
/// </summary>
public sealed class SettingsRegistry {
	private readonly Dictionary<string, SettingDefinition> _byKey = new(StringComparer.Ordinal);
	private readonly List<SettingDefinition> _ordered = [];

	/// <summary>Registers a definition. Throws if its <see cref="SettingDefinition.Key"/> is already taken.</summary>
	public void Register(SettingDefinition definition) {
		ArgumentNullException.ThrowIfNull(definition);
		if (!_byKey.TryAdd(definition.Key, definition)) {
			throw new InvalidOperationException($"Setting '{definition.Key}' is already registered.");
		}

		_ordered.Add(definition);
	}

	/// <summary>Looks up a definition by exact key.</summary>
	public bool TryGet(string key, [NotNullWhen(true)] out SettingDefinition? definition) =>
		_byKey.TryGetValue(key, out definition);

	/// <summary>Returns the definition for <paramref name="key"/>, or throws <see cref="UnknownSettingException"/>.</summary>
	public SettingDefinition Require(string key) =>
		_byKey.TryGetValue(key, out var definition) ? definition : throw new UnknownSettingException(key);

	/// <summary>All registered definitions, in registration order.</summary>
	public IReadOnlyList<SettingDefinition> Definitions => _ordered;
}
