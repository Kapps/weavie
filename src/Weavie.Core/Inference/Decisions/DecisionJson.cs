using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weavie.Core.Inference.Decisions;

internal sealed record OpenAiDecisionRequest(string Model, IReadOnlyList<OpenAiInput> Input, IReadOnlyList<OpenAiQuestion> Questions);

internal sealed record OpenAiInput(string Role, IReadOnlyList<OpenAiInputJson> Content);

internal sealed record OpenAiInputJson(string Type, JsonElement Data);

internal sealed record OpenAiQuestion(string Type, string Name, string Instructions, IReadOnlyList<OpenAiChoice> Choices);

internal sealed record OpenAiChoice(
	string Value,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description);

internal sealed record OpenAiDecisionResponse(string? Model, IReadOnlyList<OpenAiAnswer>? Answers, OpenAiUsage? Usage);

internal sealed record OpenAiAnswer(string? Type, string? Name, IReadOnlyList<OpenAiProbability>? Probabilities);

internal sealed record OpenAiProbability(string Value, double Probability);

internal sealed record OpenAiUsage(long InputTokens, OpenAiInputTokensDetails? InputTokensDetails, long OutputTokens);

internal sealed record OpenAiInputTokensDetails(long CachedTokens);

internal sealed record TypeSafeDecisionRequest(string Model, JsonElement State, IReadOnlyDictionary<string, TypeSafeQuestion> Questions);

internal sealed record TypeSafeQuestion(string Type, string Instructions, IReadOnlyDictionary<string, string?> Criteria);

internal sealed record TypeSafeDecisionResponse(string? Model, IReadOnlyDictionary<string, TypeSafeAnswer>? Answers, TypeSafeUsage? Usage);

internal sealed record TypeSafeAnswer(string? Type, IReadOnlyDictionary<string, double>? Probabilities);

internal sealed record TypeSafeUsage(long InputTokens, long OutputTokens);

/// <summary>The decision vendors' wire contracts, snake_case on both.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(OpenAiDecisionRequest))]
[JsonSerializable(typeof(OpenAiDecisionResponse))]
[JsonSerializable(typeof(TypeSafeDecisionRequest))]
[JsonSerializable(typeof(TypeSafeDecisionResponse))]
internal sealed partial class DecisionJson : JsonSerializerContext;
