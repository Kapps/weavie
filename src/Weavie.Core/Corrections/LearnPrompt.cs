using System.Text;

namespace Weavie.Core.Corrections;

/// <summary>
/// Composes the /learn analysis prompt: a fixed header teaching the agent what the corpus is and what to
/// do with it, followed by every recorded correction. Prefilled (never auto-submitted) into the primary
/// session — all reasoning over the corpus happens in the model, none in Core.
/// </summary>
public static class LearnPrompt {
	private const string Header = """
		Weavie recorded corrections the user made to your output after your turns ended — reverting hunks in
		review or hand-editing files you wrote. These edits happened outside your transcript, so you have
		never seen them. Each correction below carries the prompt that produced your output (when known) and,
		per file, a unified diff from WHAT YOU WROTE to WHAT THE USER CHANGED IT TO.

		Mine them for durable preferences about how work should be done in this repository:

		1. Look for recurring patterns — style the user keeps rewriting, approaches they keep reverting,
		   conventions your output keeps missing. A single one-off fix, an unrelated edit that merely touched
		   the same file, or another agent's concurrent work is noise: ignore it.
		2. Check CLAUDE.md (and any docs it links) first — don't propose a rule it already states; if a
		   correction shows an existing rule being violated anyway, consider whether sharpening it would help.
		3. Propose at most a handful of rules, each 1–2 lines, general enough to prevent the next occurrence
		   and specific enough to act on. Present them with the evidence (which corrections back each rule)
		   and ask me to confirm before editing CLAUDE.md. If the corrections support no durable rule, say so
		   plainly instead of inventing one.
		""";

	/// <summary>Builds the full prompt text for <paramref name="records"/> (oldest first).</summary>
	/// <param name="records">The recorded corrections to analyze.</param>
	public static string Compose(IReadOnlyList<CorrectionRecord> records) {
		ArgumentNullException.ThrowIfNull(records);
		var sb = new StringBuilder(Header);
		for (int i = 0; i < records.Count; i++) {
			var record = records[i];
			sb.Append("\n\n## Correction ").Append(i + 1).Append('\n');
			sb.Append("Prompt: ").Append(string.IsNullOrEmpty(record.Prompt) ? "(not recorded)" : record.Prompt.ReplaceLineEndings(" "));
			foreach (var file in record.Files) {
				sb.Append("\n\n### ").Append(file.Path).Append("\n```diff\n").Append(file.Delta).Append("\n```");
			}

			if (record.DroppedFiles > 0) {
				sb.Append("\n\n(").Append(record.DroppedFiles).Append(" more corrected file(s) omitted to fit the corpus cap.)");
			}
		}

		return sb.ToString();
	}
}
