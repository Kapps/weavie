using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Weavie.Core.Inference;

/// <summary>The session whose rail chip needs an emoji.</summary>
public sealed record SessionEmojiInferenceInput {
	/// <summary>The session's branch (or folder) name.</summary>
	public required string Branch { get; init; }
}

/// <summary>The structured model choice of a session's rail emoji.</summary>
public sealed record SessionEmojiInferenceOutput {
	/// <summary>Exactly one emoji depicting the session's task.</summary>
	public required string Emoji { get; init; }
}

/// <summary>The session-emoji prompt, bounds, and reply validation.</summary>
public static class SessionEmojiInference {
	private const string Instructions = "Pick exactly one emoji that depicts the task named by this git branch, so the "
		+ "user can recognise the session at a glance in a list of sessions. Prefer a concrete, visually distinctive "
		+ "object over a generic symbol, and a single-codepoint emoji widely supported since Unicode 13.";

	/// <summary>The resource policy for an automatic session-emoji query.</summary>
	public static InferenceQueryOptions QueryOptions { get; } = new() {
		Origin = InferenceInvocationOrigin.Automatic,
		MaxPromptBytes = 4 * 1024,
		MaxImageCount = 0,
		MaxImageBytes = 0,
		MaxOutputBytes = 256,
		TimeBudget = TimeSpan.FromSeconds(24),
	};

	/// <summary>The strict session-emoji response shape.</summary>
	public static JsonTypeInfo<SessionEmojiInferenceOutput> ResponseType =>
		SessionEmojiInferenceJsonContext.Default.SessionEmojiInferenceOutput;

	/// <summary>Builds the complete provider-agnostic prompt for <paramref name="branch"/>.</summary>
	public static string BuildPrompt(string branch) =>
		InferencePrompts.WithJsonInput(
			Instructions,
			new SessionEmojiInferenceInput { Branch = branch },
			SessionEmojiInferenceJsonContext.Default.SessionEmojiInferenceInput);

	/// <summary>Whether <paramref name="reply"/> is exactly one pictographic emoji.</summary>
	public static bool IsSingleEmoji(string reply) =>
		new StringInfo(reply).LengthInTextElements == 1
		&& reply.EnumerateRunes().First() is var rune
		// Emoji start at the Miscellaneous Technical block; below it, symbols like © ° ™ render as text.
		&& rune.Value >= 0x2300
		&& Rune.GetUnicodeCategory(rune) == UnicodeCategory.OtherSymbol;
}

[JsonSourceGenerationOptions(
	PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
	UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
	RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(SessionEmojiInferenceInput))]
[JsonSerializable(typeof(SessionEmojiInferenceOutput))]
internal sealed partial class SessionEmojiInferenceJsonContext : JsonSerializerContext;
