using System.Globalization;
using System.Net;
using System.Text;
using Weavie.Core.Commands;
using Weavie.Core.Corrections;
using Weavie.Core.FileSystem;
using Weavie.Core.Inference;

namespace Weavie.Hosting;

// weavie.learn.fromCorrections: analyze the workspace's recorded corrections in ONE isolated ad-hoc inference
// query and open the proposed rules in a read-only `about:corrections` tab. The user's click is the only thing
// that spends tokens, LearnSchedule holds it to one analysis a day, and the ring is consumed only once a result
// is in hand — a failed analysis costs neither the corpus nor the day. Weavie stores the signal and bounds it;
// the model does all the reasoning. See docs/specs/learn-from-corrections.md.
public sealed partial class HostCore {
	// The source-tab path/key for the analysis; never fetched (the host fills its content directly).
	private const string LearnTarget = "about:corrections";
	// The source identity stamped on the tab's messages — the web keys the tab icon off it (like ISource.Id).
	private const string LearnSourceId = "corrections";
	private const string LearnTitle = "What Your Corrections Suggest";
	private const string LearnEmpty =
		"No corrections recorded yet — after you revert or hand-edit something the agent wrote, run this again.";

	private CommandResult RunLearn(HostSession session) {
		if (SlotFor(session) is not { } slot) {
			return CommandResult.Failure("That session is no longer loaded.");
		}

		// The schedule is consulted first, and it owns the wording of both refusals. Inside the interval the last
		// analysis IS the answer to "what did my corrections say?" — the ring that produced it is already gone, so
		// this is its only copy and a flat refusal would destroy the day's result on one closed tab.
		var refusal = _learnSchedule.Claim(out string message);
		if (refusal is LearnRefusal.Cooldown && _learnSchedule.LastResult is { } previous) {
			SourceTab.Html(session, LearnTarget, LearnTitle, LearnSourceId, previous);
			session.OpenEditorOverlay(LearnTarget, "source");
			return CommandResult.Success($"{message} Reopened your most recent analysis.");
		}

		if (refusal is not LearnRefusal.None) {
			return CommandResult.Failure(message);
		}

		int pending = _corrections.Count;
		if (pending == 0) {
			_learnSchedule.Release(null); // nothing analyzed — the slot frees and the interval is untouched
			return CommandResult.Failure(LearnEmpty);
		}

		// The tab opens spinning and the query runs in the background: an analysis takes minutes, far past the
		// bus request lane's deadline, and the user should not be blocked on it.
		SourceTab.Loading(session, LearnTarget, LearnTitle, LearnSourceId);
		session.OpenEditorOverlay(LearnTarget, "source");
		if (session.Background.Run(ct => AnalyzeCorrectionsAsync(session, slot.AgentProviderId, ct)) is null) {
			// A session unloading between the claim and here admits no work, and work that never runs would hold
			// the slot for the app's life.
			_learnSchedule.Release(null);
			SourceTab.Error(session, LearnTarget, "This session is shutting down.");
			return CommandResult.Failure("That session is shutting down.");
		}

		return CommandResult.Success(
			$"Analyzing {pending} correction(s) — the proposed rules open in a tab when they're ready.");
	}

	private async Task AnalyzeCorrectionsAsync(HostSession session, string agentProviderId, CancellationToken ct) {
		(string? Result, Action Publish) outcome = (null, () => { });
		try {
			outcome = await ComposeAnalysisAsync(session, agentProviderId, ct).ConfigureAwait(false);
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception ex) {
			// The spinner is up, so EVERY failure must resolve it in the tab the user is looking at — an unreadable
			// instructions file, a malformed provider envelope, anything. Nothing here is swallowed.
			outcome = (null, () => PostLearnError(session, ex.Message));
		} finally {
			// The slot frees BEFORE the user sees an outcome, so acting on what they just read is never refused.
			_learnSchedule.Release(outcome.Result);
		}

		outcome.Publish();
	}

	// Runs the query and returns the result to keep (null when there was none) plus how to show it. Composing the
	// outcome instead of publishing it inline is what lets the caller order the release ahead of the publish.
	private async Task<(string? Result, Action Publish)> ComposeAnalysisAsync(
		HostSession session,
		string agentProviderId,
		CancellationToken ct) {
		// Snapshot rather than take: the ring is consumed by line below, only once a result exists, so a
		// correction another session appends mid-analysis survives instead of being dropped unread.
		var entries = _corrections.Snapshot();
		if (entries.Count == 0) {
			return (null, () => PostLearnError(session, LearnEmpty));
		}

		var input = new CorrectionLessonsInput {
			ExistingInstructions = CorrectionLessons.ReadInstructions(new LocalFileSystem(), session.WorkspaceRoot),
			Corrections = [.. entries.Select(entry => entry.Record)],
		};
		var result = await _inference.QueryAsync(
			new InferenceOwner { AgentProviderId = agentProviderId, Workspace = session.WorkspaceRoot },
			InferenceModelCategory.Reasoning,
			new InferenceInput { Prompt = CorrectionLessons.BuildPrompt(input), Images = [] },
			CorrectionLessons.ResponseType,
			CorrectionLessons.QueryOptions,
			ct).ConfigureAwait(false);
		if (result is InferenceFailure<CorrectionLessonsOutput> failure) {
			return (null, () => PostLearnError(session, failure.Detail));
		}

		if (result is not InferenceSuccess<CorrectionLessonsOutput> success) {
			throw new InvalidOperationException("Corrections analysis returned an unknown result type.");
		}

		// Analyzed corrections are spent whether or not they yielded a rule: re-reading the same corpus tomorrow
		// would reach the same conclusion.
		_corrections.Remove([.. entries.Select(entry => entry.Line)]);
		string html = LearnHtml(success.Value, entries.Count, success.Receipt);
		return (html, () => SourceTab.Html(session, LearnTarget, LearnTitle, LearnSourceId, html));
	}

	private static void PostLearnError(HostSession session, string message) =>
		SourceTab.Error(session, LearnTarget, $"Couldn't analyze your corrections: {message}");

	// The model's own words reach an innerHTML sink, so every value is encoded here (DOMPurify sits downstream).
	// Tags stay inside SourceView's allowlist.
	private static string LearnHtml(CorrectionLessonsOutput lessons, int analyzed, InferenceReceipt receipt) {
		var html = new StringBuilder("<div class=\"wv-learn-note\">")
			.Append(analyzed == 1 ? "1 correction" : $"{analyzed.ToString(CultureInfo.InvariantCulture)} corrections")
			.Append(" · ").Append(WebUtility.HtmlEncode(receipt.ModelId))
			.Append(" · ").Append(receipt.Duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture))
			.Append("s</div>");
		if (lessons.Summary.Length > 0) {
			html.Append("<p>").Append(WebUtility.HtmlEncode(lessons.Summary)).Append("</p>");
		}

		if (lessons.Rules.Count == 0) {
			return html.Append("<p class=\"wv-learn-note\">No durable rule came out of these corrections. "
				+ "They've been cleared; keep correcting and Weavie will ask again.</p>").ToString();
		}

		html.Append("<h2>Proposed rules</h2><ol>");
		foreach (var rule in lessons.Rules) {
			html.Append("<li><p>").Append(WebUtility.HtmlEncode(rule.Rule))
				.Append("</p><p class=\"wv-learn-evidence\">").Append(WebUtility.HtmlEncode(rule.Evidence))
				.Append("</p></li>");
		}

		// Weavie never edits AGENTS.md, so the rules also come as one paste-ready block — otherwise acting on them
		// means retyping each by hand out of the evidence list.
		html.Append("</ol><h2>Copy into AGENTS.md</h2><pre>");
		foreach (var rule in lessons.Rules) {
			html.Append("- ").Append(WebUtility.HtmlEncode(rule.Rule.ReplaceLineEndings(" "))).Append('\n');
		}

		return html.Append("</pre>").ToString();
	}
}
