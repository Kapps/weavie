using System.Diagnostics.CodeAnalysis;

namespace Weavie.Core.Commands;

/// <summary>
/// The catalog of declared commands: register a <see cref="CommandDefinition"/> once and everything downstream
/// (MCP surface, keybinding defaults, palette, NL mapping) is generated from it. Registration order is
/// preserved for a stable catalog. Core registers its commands at startup via <see cref="CoreCommands"/>.
/// </summary>
public sealed class CommandRegistry {
	private readonly OrderedDictionary<string, CommandDefinition> _definitions = new(StringComparer.Ordinal);

	/// <summary>Registers a definition. Throws if its <see cref="CommandDefinition.Id"/> is already taken.</summary>
	public void Register(CommandDefinition definition) {
		ArgumentNullException.ThrowIfNull(definition);
		if (!_definitions.TryAdd(definition.Id, definition)) {
			throw new InvalidOperationException($"Command '{definition.Id}' is already registered.");
		}
	}

	/// <summary>Looks up a definition by exact id.</summary>
	public bool TryGet(string id, [NotNullWhen(true)] out CommandDefinition? definition) =>
		_definitions.TryGetValue(id, out definition);

	/// <summary>Returns the definition for <paramref name="id"/>, or throws <see cref="UnknownCommandException"/> (with near-match suggestions).</summary>
	public CommandDefinition Require(string id) =>
		_definitions.TryGetValue(id, out var definition)
			? definition
			: throw new UnknownCommandException(id, BuildUnknownMessage(id));

	/// <summary>All registered definitions, in registration order.</summary>
	public IReadOnlyList<CommandDefinition> Definitions => _definitions.Values;

	private string BuildUnknownMessage(string id) {
		var suggestions = Suggest(id);
		string tail = suggestions.Count > 0 ? $" Did you mean: {string.Join(", ", suggestions)}?" : string.Empty;
		return $"Unknown command '{id}'. Call listCommands for the exact ids.{tail}";
	}

	// Cheap "did you mean": registered ids that contain the unknown id's leaf segment, or share its namespace
	// prefix. Enough to nudge a near-miss without a full edit-distance pass.
	private IReadOnlyList<string> Suggest(string id) {
		int lastDot = id.LastIndexOf('.');
		string leaf = lastDot >= 0 ? id[(lastDot + 1)..] : id;
		int firstDot = id.IndexOf('.');
		string? prefix = firstDot > 0 ? id[..firstDot] : null;

		return _definitions.Values
			.Select(d => d.Id)
			.Where(existing =>
				(leaf.Length > 0 && existing.Contains(leaf, StringComparison.OrdinalIgnoreCase))
				|| (prefix is not null && existing.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
			.Take(5)
			.ToList();
	}
}
