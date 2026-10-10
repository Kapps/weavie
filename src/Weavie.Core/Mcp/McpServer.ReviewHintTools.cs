using System.Text.Json;
using Weavie.Core.Json;
using Weavie.Core.Review.Hints;

namespace Weavie.Core.Mcp;

// addReviewHint: one agent review finding shown inline at its line. See docs/specs/inline-review-hints.md.
public sealed partial class McpServer {
	private async Task HandleAddReviewHintAsync(IMcpResponder responder, JsonElement args, string? idRaw, CancellationToken ct) {
		string path = args.GetStringOrEmpty("path");
		string text = args.GetStringOrEmpty("text");
		if (path.Length == 0 || text.Trim().Length == 0
			|| !args.TryGetProperty("line", out var lineValue) || lineValue.ValueKind != JsonValueKind.Number
			|| !lineValue.TryGetInt32(out int line)) {
			await SendToolErrorAsync(responder, idRaw, "addReviewHint requires 'path', an integer 'line', 'severity', and 'text'.", ct).ConfigureAwait(false);
			return;
		}

		ReviewHintSeverity severity;
		try {
			severity = ReviewHintSeverities.Parse(args.GetStringOrEmpty("severity"));
		} catch (ArgumentException ex) {
			await SendToolErrorAsync(responder, idRaw, ex.Message, ct).ConfigureAwait(false);
			return;
		}

		IReadOnlyList<string> errors;
		try {
			errors = _reviewHints.Add([ReviewFinding.FromText(path, line, severity, text)], null);
		} catch (IOException ex) {
			errors = [ex.Message];
		}
		await (errors.Count > 0
			? SendToolErrorAsync(responder, idRaw, errors[0], ct)
			: SendToolTextAsync(responder, idRaw,
				$"Hint added at {path}:{line}. Report any other findings the same way, then keep your chat reply to a brief overall summary.",
				ct)).ConfigureAwait(false);
	}
}
