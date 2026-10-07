using System.Text.Json.Serialization;

namespace Weavie.Hosting.Messaging;

/// <summary>A page toast; <see cref="Key"/> replaces a live toast in place and <see cref="Action"/> runs a command.</summary>
internal sealed record ToastMessage(
	string Level,
	string Message,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ToastAction? Action) {
	public static ToastMessage Plain(string level, string message) => new(level, message, null, null);

	public static ToastMessage Keyed(string level, string message, string key) => new(level, message, key, null);
}

/// <summary>A toast button backed by a registered command.</summary>
internal sealed record ToastAction(string Label, string CommandId, string? ArgsJson);

/// <summary>Dismisses the live toast carrying <see cref="Key"/>.</summary>
internal sealed record ToastKey(string Key);
