using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Weavie.Core.FileSystem;
using Weavie.Core.Inference;

namespace Weavie.Core.Corrections;

/// <summary>The bounded evidence one corrections analysis reasons over.</summary>
public sealed record CorrectionLessonsInput {
	/// <summary>
	/// The repository's existing agent instructions, so the analysis never re-proposes a rule they already state.
	/// Empty when the repository has none.
	/// </summary>
	public required string ExistingInstructions { get; init; }

	/// <summary>Every recorded correction, oldest first.</summary>
	public required IReadOnlyList<CorrectionRecord> Corrections { get; init; }
}

/// <summary>One durable rule the analysis proposes.</summary>
public sealed record CorrectionRule {
	/// <summary>The rule itself — one or two lines, ready to paste into the instructions file.</summary>
	public required string Rule { get; init; }

	/// <summary>The corrections that back it, named concretely enough for the user to check.</summary>
	public required string Evidence { get; init; }
}

/// <summary>The structured result of one corrections analysis.</summary>
public sealed record CorrectionLessonsOutput {
	/// <summary>The proposed rules, best-evidenced first; empty when the corrections support none.</summary>
	public required IReadOnlyList<CorrectionRule> Rules { get; init; }

	/// <summary>A sentence or two on what the corrections showed — the whole answer when <see cref="Rules"/> is empty.</summary>
	public required string Summary { get; init; }
}

/// <summary>
/// The corrections-analysis query: what the model is asked, what context travels with the ask, and the shape it
/// must answer in. Weavie stores the signal and bounds it; all reasoning over it is the model's.
/// </summary>
public static class CorrectionLessons {
	/// <summary>The byte ceiling on the repository instructions carried into the prompt.</summary>
	public const int MaxInstructionsBytes = 32 * 1024;

	// The repository's own instruction files, in the order they are presented. Ad-hoc inference runs without tools,
	// so what the model knows about existing rules is exactly what travels here.
	private static readonly string[] InstructionFiles = ["AGENTS.md", "CLAUDE.md"];

	private const string Instructions = """
		Weavie recorded corrections the user made to an agent's output after its turns ended — reverting hunks in
		review, or hand-editing files the agent wrote. These edits never entered any transcript, so no model has
		seen them. Each correction carries the prompt that produced the output (when known) and, per file, a
		unified diff FROM what the agent wrote TO what the user changed it to.

		Mine them for durable preferences about how work should be done in this repository:

		1. Look for recurring patterns — style the user keeps rewriting, approaches they keep reverting,
		   conventions the output keeps missing. A single one-off fix, an unrelated edit that merely touched the
		   same file, or another agent's concurrent work is noise: ignore it.
		2. existingInstructions is the repository's current agent instructions. Never propose a rule it already
		   states; when a correction shows an existing rule being violated anyway, propose the sharpened wording
		   and say in its evidence that it sharpens an existing rule.
		3. Propose at most five rules, best-evidenced first, each one or two lines — general enough to prevent the
		   next occurrence, specific enough to act on. Each rule's evidence names the corrections behind it.
		4. When the corrections support no durable rule, return no rules and say plainly in summary what you saw
		   instead. Never invent one.
		""";

	/// <summary>
	/// The resource policy for one analysis. The prompt ceiling is derived from the ring's own cap and the
	/// instructions ceiling (JSON escaping can up to double either), so a normal corpus can never trip it.
	/// </summary>
	public static InferenceQueryOptions QueryOptions { get; } = new() {
		// The user opens this by clicking the card or running the command, so it is never an automatic call.
		Origin = InferenceInvocationOrigin.UserInitiated,
		MaxPromptBytes = (2 * CorrectionCorpus.MaxBytes) + (2 * MaxInstructionsBytes) + (8 * 1024),
		MaxImageCount = 0,
		MaxImageBytes = 0,
		MaxOutputBytes = 16 * 1024,
		TimeBudget = TimeSpan.FromMinutes(3),
	};

	/// <summary>The strict analysis response shape.</summary>
	public static JsonTypeInfo<CorrectionLessonsOutput> ResponseType =>
		CorrectionLessonsJsonContext.Default.CorrectionLessonsOutput;

	/// <summary>Builds the complete provider-agnostic prompt for the recorded corrections.</summary>
	/// <param name="input">The instructions and corrections to analyze.</param>
	public static string BuildPrompt(CorrectionLessonsInput input) =>
		InferencePrompts.WithJsonInput(
			Instructions,
			input,
			CorrectionLessonsJsonContext.Default.CorrectionLessonsInput);

	/// <summary>
	/// Reads the repository's root agent instructions, bounded to <see cref="MaxInstructionsBytes"/>. A read that
	/// fails propagates: an analysis blind to the existing rules would quietly re-propose them.
	/// </summary>
	/// <param name="fileSystem">The filesystem to read through.</param>
	/// <param name="workspaceRoot">The repository root.</param>
	public static string ReadInstructions(IFileSystem fileSystem, string workspaceRoot) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
		var text = new StringBuilder();
		foreach (string name in InstructionFiles) {
			string path = Path.Combine(workspaceRoot, name);
			if (!fileSystem.FileExists(path) || !fileSystem.TryReadAllText(path, out string contents)) {
				continue;
			}

			text.Append(text.Length == 0 ? string.Empty : "\n\n").Append("# ").Append(name).Append("\n\n").Append(contents);
		}

		return CorrectionText.TruncateUtf8(text.ToString(), MaxInstructionsBytes);
	}
}

[JsonSourceGenerationOptions(
	PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
	UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
	RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(CorrectionLessonsInput))]
[JsonSerializable(typeof(CorrectionLessonsOutput))]
internal sealed partial class CorrectionLessonsJsonContext : JsonSerializerContext;
