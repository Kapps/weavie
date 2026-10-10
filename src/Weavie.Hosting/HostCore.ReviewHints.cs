using System.Text.Json;
using Weavie.Core.Commands;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

// A session's review hints on the `reviewHints` feature, Clear Review Hints, and the shared Fix action that sends
// one line-anchored finding (a hint or a PR thread) to the session's agent. See docs/specs/inline-review-hints.md.
public sealed partial class HostCore {
	private void WireReviewHints(HostSession session) {
		var hints = session.ReviewHints;
		var feature = session.Bus.Feature("reviewHints");
		feature.Handle("dismiss", "Dismissing a review hint", WireJson.Default.ReviewHintDismissRequest, (message, _) => {
			hints.Dismiss(message.Id);
			return Task.CompletedTask;
		});
		feature.HandleConcurrent(
			"source", "Loading a review hint's source", WireJson.Default.ReviewHintSourceRequest, WireJson.Default.ReviewHintSource,
			(message, _) => Task.FromResult(new ReviewHintSource(hints.Source(
				WorktreeRelativePath(session, message.Path) ?? throw new InvalidOperationException(NotInWorktree(session, message.Path)),
				message.Hash))));
		session.Commands.RegisterHandler(CoreCommands.ReviewHintsClear, (_, _) => {
			hints.Clear();
			return Task.FromResult(CommandResult.Success("Cleared the review hints."));
		});
		session.Commands.RegisterHandler(CoreCommands.AgentFixLine, (argsJson, _) => Task.FromResult(FixLine(session, argsJson)));
	}

	// Submitted like a user prompt: a busy structured agent queues or steers it, and Claude's TUI queues typed input.
	private CommandResult FixLine(HostSession session, string? argsJson) {
		FixLineArgs? args;
		try {
			args = JsonSerializer.Deserialize(argsJson ?? "null", WireJson.Default.FixLineArgs);
		} catch (JsonException ex) {
			return CommandResult.Failure($"Fix Line arguments are malformed: {ex.Message}");
		}
		if (args is not { Path: { Length: > 0 } path, Line: > 0, Quote: not null, Text.Length: > 0, Instruction: not null }) {
			return CommandResult.Failure("Fix Line requires 'path', a 1-based 'line', 'quote', 'text', and 'instruction'.");
		}
		if (WorktreeRelativePath(session, path) is not { } relative) return CommandResult.Failure(NotInWorktree(session, path));
		if (_drainInputFrozen) return CommandResult.Failure("Agent input is paused while Weavie restarts.");

		string instruction = args.Instruction.Trim().Length == 0 ? string.Empty : $"\n\nAdditional instruction: {args.Instruction.Trim()}";
		string fence = CodeFence(args.Quote);
		try {
			session.SendAgentPrompt(
				$"Fix this review finding at `{relative}:{args.Line}`:\n\n{fence}\n{args.Quote}\n{fence}\n\n{args.Text.Trim()}{instruction}");
		} catch (InvalidOperationException ex) {
			return CommandResult.Failure(ex.Message);
		}
		return CommandResult.Success($"Asked the agent to fix {relative}:{args.Line}.");
	}

	// A Markdown fence longer than any backtick run in the text, so the text can never close it.
	internal static string CodeFence(string text) {
		int longest = 0;
		for (int run = 0, i = 0; i < text.Length; i++) {
			run = text[i] == '`' ? run + 1 : 0;
			longest = Math.Max(longest, run);
		}
		return new string('`', Math.Max(3, longest + 1));
	}

	internal sealed record ReviewHintDismissRequest(long Id);

	internal sealed record ReviewHintSourceRequest(string Path, string Hash);

	internal sealed record ReviewHintSource(string Text);

	internal sealed record FixLineArgs(string Path, int Line, string Quote, string Text, string Instruction);
}
