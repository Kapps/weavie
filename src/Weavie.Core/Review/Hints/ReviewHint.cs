namespace Weavie.Core.Review.Hints;

/// <summary>How serious an agent judged a review finding.</summary>
public enum ReviewHintSeverity {
	/// <summary>A defect.</summary>
	Bug,

	/// <summary>A risk worth a look.</summary>
	Warn,

	/// <summary>A minor suggestion.</summary>
	Nit,
}

/// <summary>Converts <see cref="ReviewHintSeverity"/> to and from the lower-case names agents and the page use.</summary>
public static class ReviewHintSeverities {
	/// <summary>The severity's name: <c>bug</c>, <c>warn</c>, or <c>nit</c>.</summary>
	public static string Name(ReviewHintSeverity severity) => severity switch {
		ReviewHintSeverity.Bug => "bug",
		ReviewHintSeverity.Warn => "warn",
		ReviewHintSeverity.Nit => "nit",
		_ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unknown review hint severity."),
	};

	/// <summary>Parses a severity name exactly; anything else is an <see cref="ArgumentException"/>.</summary>
	public static ReviewHintSeverity Parse(string name) => name switch {
		"bug" => ReviewHintSeverity.Bug,
		"warn" => ReviewHintSeverity.Warn,
		"nit" => ReviewHintSeverity.Nit,
		_ => throw new ArgumentException($"Severity must be bug, warn, or nit, not '{name}'.", nameof(name)),
	};
}

/// <summary>One line-anchored finding as an agent reported it; <paramref name="Path"/> is unresolved.</summary>
public sealed record ReviewFinding(
	string Path,
	int Line,
	ReviewHintSeverity Severity,
	string Title,
	string Body,
	string? Category) {
	/// <summary>A finding whose <paramref name="text"/>'s first line is the title and the rest its body.</summary>
	public static ReviewFinding FromText(string path, int line, ReviewHintSeverity severity, string text) {
		ArgumentNullException.ThrowIfNull(text);
		string[] parts = text.Trim().ReplaceLineEndings("\n").Split('\n', 2);
		return new(path, line, severity, parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : string.Empty, null);
	}
}

/// <summary>
/// A captured review finding. <paramref name="Path"/> is worktree-relative with <c>/</c> separators;
/// <paramref name="SourceHash"/> names the file text it was captured against and <paramref name="Quote"/> is its line.
/// </summary>
public sealed record ReviewHint(
	long Id,
	long Batch,
	string Path,
	int Line,
	ReviewHintSeverity Severity,
	string Title,
	string Body,
	string? Category,
	string Agent,
	string SourceHash,
	string Quote);
