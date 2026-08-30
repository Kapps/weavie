using System.Diagnostics.CodeAnalysis;

namespace Weavie.Core.Sessions;

/// <summary>The stable identity of one shell terminal tab in a session.</summary>
public sealed record ShellTerminalDescriptor {
	/// <summary>The terminal id, unique within its session and stable across unload/reload.</summary>
	public required string Id { get; init; }

	/// <summary>Creates a descriptor with a new stable identity.</summary>
	public static ShellTerminalDescriptor New() => new() { Id = Guid.NewGuid().ToString("n") };

	/// <summary>Whether <paramref name="id"/> has the generated, path-safe lowercase GUID format.</summary>
	public static bool IsValidId([NotNullWhen(true)] string? id) =>
		id is { Length: 32 }
		&& id.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
