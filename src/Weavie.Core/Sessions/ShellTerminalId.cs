using System.Diagnostics.CodeAnalysis;

namespace Weavie.Core.Sessions;

/// <summary>Creates and validates stable shell terminal tab IDs.</summary>
public static class ShellTerminalId {
	/// <summary>Creates a new path-safe terminal ID.</summary>
	public static string New() => Guid.NewGuid().ToString("n");

	/// <summary>Whether <paramref name="id"/> has the generated path-safe lowercase GUID format.</summary>
	public static bool IsValid([NotNullWhen(true)] string? id) =>
		id is { Length: 32 }
		&& id.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
