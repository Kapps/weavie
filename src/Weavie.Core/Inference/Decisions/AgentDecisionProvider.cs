using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weavie.Core.Inference.Decisions;

/// <summary>Answers a decision with a typed query through the live inference agent, one number per label.</summary>
public sealed partial class AgentDecisionProvider : IDecisionProvider {
	private const string Instructions = "You decide one step of a software workflow. For every supplied choice label, "
		+ "estimate the probability between 0 and 1 that it is the right answer to the question, given the supplied "
		+ "evidence. Use each choice's description to distinguish its criteria, and report low probabilities when the "
		+ "evidence is insufficient. Return only the requested JSON; no explanations or tools.";

	private readonly IInferenceService _inference;

	/// <summary>Creates the provider over the typed inference service.</summary>
	public AgentDecisionProvider(IInferenceService inference) {
		ArgumentNullException.ThrowIfNull(inference);
		_inference = inference;
	}

	/// <inheritdoc/>
	public string Id => DecisionProviderIds.Agent;

	/// <inheritdoc/>
	public string Label => "Agent";

	/// <inheritdoc/>
	public string Detail => "The configured inference agent";

	/// <inheritdoc/>
	public bool NeedsCredential => false;

	/// <inheritdoc/>
	public async Task<DecisionProviderResult> DecideAsync(DecisionProviderRequest request, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(request);
		var input = new AgentDecisionInput(request.Question.Instructions, request.Question.Choices, request.Evidence);
		var result = await _inference.QueryAsync(
			request.Owner,
			InferenceModelCategory.Utility,
			new InferenceInput {
				Prompt = InferencePrompts.WithJsonInput(Instructions, input, AgentDecisionJson.Default.AgentDecisionInput),
				Images = [],
			},
			new InferenceResponseType<Dictionary<string, double>>(
				AgentDecisionJson.Default.DictionaryStringDouble,
				Schema(request.Question)),
			new InferenceQueryOptions {
				Origin = request.Origin,
				MaxImageCount = 0,
				MaxImageBytes = 0,
				MaxOutputBytes = 1024,
				TimeBudget = request.TimeBudget,
			},
			ct).ConfigureAwait(false);
		return result switch {
			InferenceSuccess<Dictionary<string, double>> success => new DecisionDistribution {
				ModelId = success.Receipt.ModelId,
				RequestId = success.Receipt.RequestId,
				Usage = success.Receipt.Usage,
				Probabilities = success.Value,
			},
			InferenceFailure<Dictionary<string, double>> failure => new DecisionProviderFailure {
				ModelId = failure.Receipt?.ModelId ?? "",
				RequestId = failure.Receipt?.RequestId,
				Usage = failure.Receipt?.Usage,
				Kind = failure.Kind,
				Detail = failure.Detail,
			},
			_ => throw new InvalidOperationException("Unknown inference result."),
		};
	}

	// One required probability per label and nothing else, so the reply can't name an answer outside the choices.
	private static string Schema(DecisionQuestion question) {
		using var buffer = new MemoryStream();
		using (var json = new Utf8JsonWriter(buffer)) {
			json.WriteStartObject();
			json.WriteString("type", "object");
			json.WriteStartObject("properties");
			foreach (var choice in question.Choices) {
				json.WriteStartObject(choice.Label);
				json.WriteString("type", "number");
				json.WriteNumber("minimum", 0);
				json.WriteNumber("maximum", 1);
				json.WriteEndObject();
			}
			json.WriteEndObject();
			json.WriteStartArray("required");
			foreach (var choice in question.Choices) {
				json.WriteStringValue(choice.Label);
			}
			json.WriteEndArray();
			json.WriteBoolean("additionalProperties", false);
			json.WriteEndObject();
		}
		return Encoding.UTF8.GetString(buffer.ToArray());
	}

	internal sealed record AgentDecisionInput(string Question, IReadOnlyList<DecisionChoice> Choices, JsonElement Evidence);

	[JsonSourceGenerationOptions(
		PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
		RespectRequiredConstructorParameters = true)]
	[JsonSerializable(typeof(AgentDecisionInput))]
	[JsonSerializable(typeof(Dictionary<string, double>))]
	internal sealed partial class AgentDecisionJson : JsonSerializerContext;
}
