using System.Text.Json;

namespace Weavie.Core.Review.Hints;

/// <summary>Reads the <c>tool_input</c> of Claude's built-in <c>ReportFindings</c> tool.</summary>
public static class ClaudeReportFindings {
	/// <summary>The tool's name in Claude's hook payloads.</summary>
	public const string ToolName = "ReportFindings";

	/// <summary>
	/// Parses every finding strictly; a malformed payload throws <see cref="FormatException"/> rather than dropping any.
	/// <c>verdict</c> <c>PLAUSIBLE</c> is a warning; <c>CONFIRMED</c> or none is a bug.
	/// </summary>
	public static IReadOnlyList<ReviewFinding> Parse(string toolInputJson) {
		try {
			using var document = JsonDocument.Parse(toolInputJson);
			if (document.RootElement.ValueKind != JsonValueKind.Object
				|| !document.RootElement.TryGetProperty("findings", out var findings)
				|| findings.ValueKind != JsonValueKind.Array) {
				throw new FormatException("ReportFindings input has no 'findings' array.");
			}
			return [.. findings.EnumerateArray().Select(Finding)];
		} catch (JsonException ex) {
			throw new FormatException($"ReportFindings input is not valid JSON: {ex.Message}", ex);
		}
	}

	private static ReviewFinding Finding(JsonElement finding) {
		if (finding.ValueKind != JsonValueKind.Object) throw new FormatException("A ReportFindings finding is not an object.");
		string summary = Required(finding, "summary");
		string title = Optional(finding, "short_summary") ?? summary;
		string body = string.Join("\n\n", new[] { title == summary ? null : summary, Optional(finding, "failure_scenario") }
			.Where(part => !string.IsNullOrWhiteSpace(part)));
		var severity = Optional(finding, "verdict") switch {
			null or "CONFIRMED" => ReviewHintSeverity.Bug,
			"PLAUSIBLE" => ReviewHintSeverity.Warn,
			string other => throw new FormatException($"A ReportFindings finding has an unknown verdict '{other}'."),
		};
		if (!finding.TryGetProperty("line", out var line) || !line.TryGetInt32(out int number)) {
			throw new FormatException("A ReportFindings finding has no integer 'line'.");
		}
		return new(Required(finding, "file"), number, severity, title, body, Optional(finding, "category"));
	}

	private static string Required(JsonElement finding, string name) =>
		Optional(finding, name) is { Length: > 0 } value
			? value
			: throw new FormatException($"A ReportFindings finding has no '{name}'.");

	private static string? Optional(JsonElement finding, string name) {
		if (!finding.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
		return value.ValueKind == JsonValueKind.String
			? value.GetString()
			: throw new FormatException($"A ReportFindings finding's '{name}' is not a string.");
	}
}
