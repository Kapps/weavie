using Weavie.Core.Mcp;

namespace Weavie.Core.Review.Hints;

/// <summary>Where agent-reported review findings land.</summary>
public interface IReviewHintSink {
	/// <summary>
	/// Adds each valid finding not already shown, resolving relative paths against <paramref name="cwd"/> (or the
	/// worktree when null), and returns why each rejected finding was refused.
	/// </summary>
	IReadOnlyList<string> Add(IReadOnlyList<ReviewFinding> findings, string? cwd);
}

/// <summary>Review hint sinks for servers that have none.</summary>
public static class ReviewHintSink {
	/// <summary>A sink for MCP servers that never advertise <c>addReviewHint</c>; every add is a tool error.</summary>
	public static IReviewHintSink None { get; } = new Unavailable();

	private sealed class Unavailable : IReviewHintSink {
		public IReadOnlyList<string> Add(IReadOnlyList<ReviewFinding> findings, string? cwd) =>
			throw new ToolUnavailableException("Review hints are not available.");
	}
}
