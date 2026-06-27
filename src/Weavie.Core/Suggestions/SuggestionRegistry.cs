using System.Diagnostics.CodeAnalysis;

namespace Weavie.Core.Suggestions;

/// <summary>
/// The catalog of declared suggestions: register a <see cref="SuggestionDefinition"/> once and
/// <see cref="SuggestionService"/> evaluates it against the workspace. Registration order is preserved for a
/// stable card order.
/// </summary>
public sealed class SuggestionRegistry {
	private readonly Dictionary<string, SuggestionDefinition> _byId = new(StringComparer.Ordinal);
	private readonly List<SuggestionDefinition> _ordered = [];

	/// <summary>Registers a definition. Throws if its <see cref="SuggestionDefinition.Id"/> is already taken.</summary>
	public void Register(SuggestionDefinition definition) {
		ArgumentNullException.ThrowIfNull(definition);
		if (!_byId.TryAdd(definition.Id, definition)) {
			throw new InvalidOperationException($"Suggestion '{definition.Id}' is already registered.");
		}

		_ordered.Add(definition);
	}

	/// <summary>Looks up a definition by exact id.</summary>
	public bool TryGet(string id, [NotNullWhen(true)] out SuggestionDefinition? definition) =>
		_byId.TryGetValue(id, out definition);

	/// <summary>All registered definitions, in registration order.</summary>
	public IReadOnlyList<SuggestionDefinition> Definitions => _ordered;
}
