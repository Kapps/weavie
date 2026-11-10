using System.Text;
using System.Text.RegularExpressions;

namespace Weavie.Workflows;

/// <summary>A run directory, input, or stage output reference inside a template.</summary>
public sealed record WorkflowReference(string Scope, string Name) {
	/// <summary>The scope naming the run's inputs rather than a stage.</summary>
	public const string Inputs = "inputs";

	/// <summary>Whether this names a run input.</summary>
	public bool IsInput => Scope == Inputs;

	/// <summary>Whether this names the shared documents directory.</summary>
	public bool IsDocuments => Scope == "run" && Name == "documents";

	/// <inheritdoc/>
	public override string ToString() => $"{{{{{Scope}.{Name}}}}}";
}

/// <summary>Text with <c>{{…}}</c> references to run inputs and stage outputs; no expressions.</summary>
public sealed partial class WorkflowTemplate {
	private readonly IReadOnlyList<object> _parts;

	private WorkflowTemplate(string source, IReadOnlyList<object> parts) {
		Source = source;
		_parts = parts;
	}

	/// <summary>The template text as written.</summary>
	public string Source { get; }

	/// <summary>The empty template; stands in for one that failed to parse, whose error is already reported.</summary>
	public static WorkflowTemplate Empty { get; } = new("", [""]);

	/// <summary>Every reference, in order.</summary>
	public IEnumerable<WorkflowReference> References => _parts.OfType<WorkflowReference>();

	[GeneratedRegex(@"^\s*([a-z0-9]+(?:-[a-z0-9]+)*)\.([A-Za-z][A-Za-z0-9]*)\s*$")]
	private static partial Regex ReferencePattern();

	/// <summary>Parses <paramref name="text"/>, or explains why it isn't a valid template.</summary>
	public static bool TryParse(string text, out WorkflowTemplate template, out string error) {
		var parts = new List<object>();
		int at = 0;
		while (true) {
			int open = text.IndexOf("{{", at, StringComparison.Ordinal);
			if (open < 0) {
				parts.Add(text[at..]);
				break;
			}

			int close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
			if (close < 0) {
				return Fail("\"{{\" has no closing \"}}\"", out template, out error);
			}

			var match = ReferencePattern().Match(text[(open + 2)..close]);
			if (!match.Success) {
				return Fail(
					$"\"{text[open..(close + 2)]}\" must be {{{{run.documents}}}}, {{{{inputs.<name>}}}} or {{{{<stage>.<output>}}}}",
					out template, out error);
			}

			parts.Add(text[at..open]);
			parts.Add(new WorkflowReference(match.Groups[1].Value, match.Groups[2].Value));
			at = close + 2;
		}

		template = new WorkflowTemplate(text, parts);
		error = "";
		return true;
	}

	/// <summary>Renders with each reference replaced by its resolved value.</summary>
	public string Render(Func<WorkflowReference, WorkflowValue> resolve) {
		var text = new StringBuilder();
		foreach (object part in _parts) {
			text.Append(part is WorkflowReference reference ? resolve(reference).Render() : (string)part);
		}

		return text.ToString();
	}

	private static bool Fail(string message, out WorkflowTemplate template, out string error) {
		template = null!;
		error = message;
		return false;
	}
}
