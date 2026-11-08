using System.Text.RegularExpressions;

namespace Weavie.Workflows;

internal static partial class WorkflowEvidenceWarnings {
	public static void CheckQuestion(WorkflowQuestion question, string path, bool seesOutputs, WorkflowCheck check) {
		if (question.By != WorkflowAnswerer.Model) return;
		if (!seesOutputs && !question.Ask.References.Any()) {
			check.Warn(path, "the model answering this has no tools and sees only its question, referenced values and file contents, "
				+ "stage output values, and completion summary with feedback; reference the evidence it needs with {{…}}");
		}

		// Placeholders preserve composed paths without interpreting runtime values or reparsing templates.
		string text = question.Ask.Render(_ => new WorkflowValue { Text = "reference" });
		if (Tokens().Matches(text).Any(match => match.Groups["local"].Success && LooksLikePath(match.Value))) {
			check.Warn(path, "path-like text in this question does not supply file contents; use a complete reference to a files "
				+ "input or output when its contents are needed. Literal paths and suffixes on references are not read");
		}
	}

	public static void CheckOutputs(WorkflowDefinition definition, WorkflowStage stage, WorkflowQuestion question, string path, WorkflowCheck check) {
		if (question.By != WorkflowAnswerer.Model) return;
		var referenced = question.Ask.References
			.Where(r => WorkflowReferences.TypeOf(definition, r) == WorkflowValueType.Files).ToHashSet();
		foreach (var output in stage.Run.Outputs.Where(o => o.Type == WorkflowValueType.Files)) {
			var reference = new WorkflowReference(stage.Id, output.Name);
			if (!referenced.Contains(reference)) {
				check.Warn(path, $"files output {reference} is not referenced by this model question; its contents will not be read");
			}
		}
	}

	private static bool LooksLikePath(string text) =>
		(text.IndexOfAny(['/', '\\']) >= 0 && text.Any(char.IsLetter)) || FileName().IsMatch(text);

	[GeneratedRegex("""https?://[^\s<>"'`]+|(?<local>[^\s<>\[\]{}()"'`]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex Tokens();

	[GeneratedRegex(@"(^|[\p{L}\p{N}_-])\.[\p{L}][\p{L}\p{N}_-]*")]
	private static partial Regex FileName();
}
