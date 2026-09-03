using System.Text;
using System.Text.Json;
using Weavie.Core.Changes;
using Weavie.Core.Commands;
using Weavie.Core.Editor;
using Weavie.Core.FileActivity;
using Weavie.Core.FileSystem;
using Weavie.Core.Git;
using Weavie.Core.Json;
using Weavie.Core.Layout;
using Weavie.Core.Lsp;
using Weavie.Core.Remote;
using Weavie.Core.Sessions;
using Weavie.Core.Shell;
using Weavie.Core.Theming;
using Weavie.Core.Workspaces;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private void OnWebMessage(WebPeer peer, string json) => _messageIngress.Enqueue(peer, json);

	private void OnWebPeerDisconnected(WebPeer peer) => _messageIngress.EnqueueDisconnect(peer);

	internal async Task<MessageHealthSnapshot> MessageHealthAsync(CancellationToken ct) {
		try {
			await _messageIngress.ProbeAsync(ct).ConfigureAwait(false);
			return _messages.Health(ingressResponsive: true);
		} catch (Exception ex) {
			Log($"[message] ingress health probe failed: {ex.Message}");
			return _messages.Health(ingressResponsive: false);
		}
	}

	internal async Task DrainMessageIngressAsync(CancellationToken ct) {
		await _messageIngress.ProbeAsync(ct).ConfigureAwait(false);
		await _messages.DrainAsync().WaitAsync(ct).ConfigureAwait(false);
		if (_sessions is { } sessions) {
			foreach (var slot in sessions.Slots) {
				if (slot.Session is { } session) {
					await session.Agent.DrainPaneAsync(ct).ConfigureAwait(false);
				}
			}
		}
		await _messageIngress.ProbeAsync(ct).ConfigureAwait(false);
	}

	/// <summary>Applies a layout the web sent (split/focus change) through the store, which validates + persists it.</summary>
	private void HandleLayoutChanged(JsonElement root) {
		if (!root.TryGetProperty("document", out var documentElement)) {
			return;
		}

		if (!LayoutSerialization.TryDeserialize(documentElement.GetRawText(), out var document, out string? error)
			|| document is null) {
			Log($"[weavie] layout-changed: bad document ({error})");
			return;
		}

		try {
			_layout.SetPanes(document.Root, document.Focused, LayoutSource.User);
		} catch (LayoutValidationException ex) {
			Log($"[weavie] layout-changed rejected: {ex.Message}");
		}
	}

	/// <summary>
	/// Pushes the persisted remote-agent registry (with each runner's URL + token) so the page connects to each
	/// agent and offers it as a New Session location. The host owns persistence; the web owns the connections.
	/// </summary>
	private void PushRemoteAgentsToWeb() =>
		_messages.Host.Feature("remoteAgents").Publish("changed", new {
			agents = _remoteAgents.Agents.Select(a => new { name = a.Name, url = a.Url, token = a.Token }),
		});

	/// <summary>
	/// Pushes the session rail's persisted UI state (last-used backend + promoted remote sessions) so the page
	/// restores its working set and the New Session prompt's default location. Honored only from the local backend.
	/// </summary>
	private void PushRailStateToWeb() =>
		_messages.Host.Feature("rail").Publish("changed", new {
			lastLocation = _railState.LastLocation,
			promoted = _railState.Promoted,
			selected = RailSelectionSnapshot(),
		});

	/// <summary>
	/// Pushes the persisted find-in-files state (match options + include/exclude globs + recent terms) so the
	/// panel restores the user's last search mode and history — never the search term itself. Honored only from
	/// the local backend (it's a local-machine file).
	/// </summary>
	private void PushSearchStateToWeb() {
		var state = _searchState.Current;
		_messages.Host.Feature("search").Publish("state", new {
			options = new {
				caseSensitive = state.Options.CaseSensitive,
				wholeWord = state.Options.WholeWord,
				regex = state.Options.Regex,
				excludeGitignored = state.Options.ExcludeGitignored,
				include = state.Options.Include,
				exclude = state.Options.Exclude,
			},
			recentTerms = state.RecentTerms,
		});
	}

	/// <summary>Pushes the app-global recent workspace list after another window reorders or prunes it.</summary>
	private void PushRecentWorkspacesToWeb() =>
		_messages.Host.Feature("recentWorkspaces").Publish("changed", new {
			recents = _platform.Recents,
		});

	/// <summary>Pushes the persisted/reconciled layout document to the web app as a compact set-layout message.</summary>
	private void PushLayoutToWeb() {
		string documentJson = LayoutSerialization.SerializeCompact(_layout.Current);
		_messages.Host.Feature("layout").PublishJson("state", $"{{\"document\":{documentJson}}}");
	}

	/// <summary>Applies editor state sent through its owning session bus.</summary>
	private bool TryHandleEditorSessionChanged(
		HostSession target,
		JsonElement sessionElement,
		out string? error) {
		if (!EditorSessionSerialization.TryDeserialize(
			sessionElement.GetRawText(),
			out var session,
			out string? deserializeError)
			|| session is null) {
			error = deserializeError;
			return false;
		}

		target.EditorSession = session;
		error = null;
		return true;
	}

	/// <summary>
	/// Publishes the owning session's language-server configuration.
	/// </summary>
	private void PushLspConfigToWeb(HostSession session) =>
		PushLspConfigToWeb(session, session.Bus.BroadcastTarget);

	private static void PushLspConfigToWeb(HostSession session, MessageTarget target) =>
		target.Feature("lsp").PublishJson("config", session.LspConfigJson);

	/// <summary>
	/// Re-walks one session's worktree and publishes its file index. An invalidating refresh first clears only
	/// that session's prior index; a slow result remains addressed to the same owner.
	/// </summary>
	private void PushFileIndexToWeb(HostSession session, bool invalidate) =>
		PushFileIndexToWeb(session, invalidate, session.Bus.BroadcastTarget);

	private void PushFileIndexToWeb(
		HostSession session,
		bool invalidate,
		MessageTarget target) {
		// `home` anchors the omnibar's `~/…` open-by-path expansion against the *host's* profile, not the browser's.
		object Payload(IReadOnlyList<string> files, bool pending) => new {
			root = session.FileIndex.Root,
			home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			files,
			pending,
		};

		if (invalidate) {
			target.Feature("files").Publish("index", Payload([], true));
		}

		_ = session.Background.Run(async ct => {
			IReadOnlyList<string> files;
			try {
				var inventory = await session.Inventory.RefreshAsync(ct).ConfigureAwait(false);
				if (inventory.IsRepository) {
					files = inventory.Files;
				} else {
					var seed = await session.Inventory.BeginNonRepositorySeedAsync(ct).ConfigureAwait(false);
					bool completed = false;
					try {
						var navigation = session.FileIndex.ListSnapshot();
						var completedInventory = session.Inventory.CompleteNonRepositorySeed(
							seed,
							navigation.Files,
							navigation.Directories);
						files = completedInventory.Files;
						completed = true;
					} finally {
						if (!completed) {
							session.Inventory.CancelNonRepositorySeed(seed);
						}
					}
				}
			} catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException) {
				target.Feature("files").Publish("index", Payload([], false));
				Notify(session, "error", $"Couldn't load workspace files: {ex.Message}");
				return;
			}

			ct.ThrowIfCancellationRequested();
			target.Feature("files").Publish("index", Payload(files, false));
		});
	}

	// How many recent files to push: enough to power the recency tiebreak across a working set, of which the
	// omnibar renders the top few as its Recent section.
	private const int RecentFilesPushCount = 50;

	/// <summary>
	/// Records a visit as a checkout-relative path in the workspace-wide recent-files store. The page resolves
	/// that path against whichever session is selected, so recency follows a file across worktrees.
	/// </summary>
	private void RecordRecentFile(HostSession session, ActiveEditor editor) {
		// A file outside the checkout has no checkout-relative path, so it stays out rather than becoming "../..".
		if (!PathBoundary.Contains(session.WorkspaceRoot, editor.FilePath)) {
			return;
		}

		string path = Path.GetRelativePath(session.WorkspaceRoot, editor.FilePath)
			.Replace(Path.DirectorySeparatorChar, '/');
		if (string.Equals(path, _lastRecentPath, StringComparison.Ordinal)) {
			return;
		}

		_lastRecentPath = path;
		_recentFiles.Record(path, DateTime.UtcNow.Ticks);
		PushRecentFilesToWeb();
	}

	/// <summary>Pushes the frecency-ranked recent files (most-relevant first) for the omnibar's Recent section.</summary>
	private void PushRecentFilesToWeb() =>
		_messages.Host.Feature("recentFiles").Publish("changed", new {
			files = _recentFiles.Top(RecentFilesPushCount, DateTime.UtcNow.Ticks),
		});

	/// <summary>
	/// Pushes the per-turn change list (each changed file + its first-change line) for the page's review walk +
	/// parked navigator. Driven by the change tracker, which records edits in every permission mode, so it's the
	/// review surface in default mode too. The page surfaces the review itself (parked, editor untouched) — the
	/// host no longer decides an auto-open.
	/// </summary>
	private void PushTurnChangesToWeb(HostSession session) =>
		PushTurnChangesToWeb(session, session.Bus.BroadcastTarget);

	private void PushTurnChangesToWeb(HostSession session, MessageTarget target) =>
		target.Feature("review").PublishJson(
			"changes",
			ChangeMessages.TurnChanges(session.Changes, ActiveReview(session)?.Label ?? string.Empty));

	/// <summary>
	/// Replays one session's full review set after its client requests a sync: the changed-file list (navigator),
	/// each file's inline diff, and the undo/redo history. The live turn pushes only reach an already-connected
	/// client, so without this a page that connects after the changes landed — a reload, or a slow first connect —
	/// shows no review surface after a reconnect. A no-op when nothing is pending.
	/// </summary>
	private void PushReviewStateToWeb(HostSession session, MessageTarget target) {
		var changes = session.Changes.TurnChanges();
		if (changes.Count > 0) {
			PushTurnChangesToWeb(session, target);
		}
		foreach (var change in changes) {
			PushReviewFileToWeb(session, change.Path, target);
		}

		PushReviewHistoryToWeb(session, target);
	}

	/// <summary>Pushes a live refresh so VSCode reloads the non-dirty model from disk.</summary>
	private static void PushRefreshToWeb(HostSession session, string path) =>
		session.Bus.Feature("files").Publish(
			"changed",
			new { changes = new[] { new FileProviderChange(path, "updated") } });

	/// <summary>
	/// Pushes a removal for a file deleted mid-turn so the page closes its tab and clears the inline marker.
	/// Tracker-reported deletion also covers the external scratch root, which the workspace watcher does not own.
	/// </summary>
	private static void PushDeletionToWeb(HostSession session, string path) =>
		session.Bus.Feature("files").Publish(
			"changed",
			new { changes = new[] { new FileProviderChange(path, "deleted") } });

	/// <summary>Forwards a workspace-watcher batch (non-Claude on-disk edits) to the page's <c>file://</c> provider.</summary>
	private static void PushWatcherChangesToWeb(
		HostSession session,
		IReadOnlyList<FileInvalidation> changes) {
		var mapped = FileProviderChanges.FromInvalidations(changes);
		if (mapped.Length > 0) {
			session.Bus.Feature("files").Publish("changed", new { changes = mapped });
		}
	}

	/// <summary>
	/// Pushes one file's per-turn diff so the page renders it inline. Driven by the change tracker (which records
	/// edits in every mode), so the inline markers are the review surface in default mode too.
	/// </summary>
	private static void PushTurnDiffToWeb(HostSession session, string path) =>
		PushTurnDiffToWeb(session, path, session.Bus.BroadcastTarget);

	private static void PushTurnDiffToWeb(
		HostSession session,
		string path,
		MessageTarget target) {
		if (session.Changes.GetTurn(path) is { } turn) {
			target.Feature("review").PublishJson("diff", ChangeMessages.TurnDiff(turn));
		}
	}

	/// <summary>
	/// Keep-all: advances every tracked file's review baseline to current, clearing the page's inline markers and
	/// pushing the now-empty review set so the ← / → file walk empties too (the debt-clearing action).
	/// </summary>
	private void AcceptTurn(HostSession session) {
		session.Changes.AcceptTurn();
		// Keep-all commits the board: a local "diff against" review is done, so drop it — else its label would
		// cling to the next plain turn. A PR review persists (its identity + comments outlive an equal tree).
		if (ActiveReview(session) is { PrNumber: 0 }) {
			_diffReviews.TryRemove(session.WorkspaceRoot, out _);
		}

		session.Bus.Feature("review").PublishJson("reset", ChangeMessages.TurnReset());
		PushTurnChangesToWeb(session);
		PushReviewHistoryToWeb(session);
	}

	/// <summary>
	/// Undoes the whole review set: reverts every changed file to its review baseline on disk and live-refreshes
	/// the editor. The delete-vs-truncate rule lives in <see cref="SessionChangeTracker.RevertFile"/> (shared by
	/// per-hunk/per-file/whole-set reverts); the host only owns the editor pushes.
	/// </summary>
	private void UndoTurn(HostSession session) {
		try {
			ApplyHistoryResult(session, session.Changes.RevertAll());
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Notify(session, "warn", $"Couldn't revert all changes: {ex.Message}");
		}
	}

	/// <summary>
	/// Undoes a review action: <c>kind</c> "keep"/"revert" drives the type-split chords, an absent kind the
	/// toolbar's generic Undo. A blocked undo (a newer edit moved the file) toasts; otherwise the editor refreshes.
	/// </summary>
	private void ReviewUndo(HostSession session, JsonElement root) {
		string? kind = root.GetStringOrNull("kind");
		var result = kind switch {
			"keep" => session.Changes.UndoLastKeep(),
			"revert" => session.Changes.UndoLastRevert(),
			_ => session.Changes.UndoLast(),
		};
		HandleHistory(session, result);
		RevealHistoryChange(session, result);
	}

	/// <summary>Redoes the most recently undone review action (the toolbar/palette Redo).</summary>
	private void ReviewRedo(HostSession session) {
		var result = session.Changes.Redo();
		HandleHistory(session, result);
		RevealHistoryChange(session, result);
	}

	/// <summary>
	/// After an undo/redo, land the editor on the change it acted on: a per-hunk action opens at that hunk's
	/// recorded line (still valid — the undo only runs while the file's current content is unchanged), a
	/// file/set action at the first affected file's first pending hunk. No-op when nothing is left to show.
	/// </summary>
	private static void RevealHistoryChange(HostSession session, ReviewHistoryResult result) {
		if (!result.Acted) {
			return;
		}

		foreach (string path in result.Paths) {
			if (session.Changes.GetTurn(path) is { } turn
				&& (result.Line ?? LineDiff.FirstChangedLine(turn.BaselineText, turn.CurrentText)) is { } line) {
				session.FileOpener.Open(path, line, preview: true, scratch: false);
				return;
			}
		}
	}

	/// <summary>
	/// Applies an undo/redo outcome: a blocked result (a newer edit is in the way) toasts and re-pushes
	/// availability; an action that ran refreshes each affected file and the review set via <see cref="ApplyHistoryResult"/>.
	/// </summary>
	private void HandleHistory(HostSession session, ReviewHistoryResult result) {
		if (result.Acted) {
			ApplyHistoryResult(session, result);
			return;
		}

		if (result.WasBlocked) {
			Notify(session, "warn", "That change moved since — re-open to review before undoing it.");
		}

		PushReviewHistoryToWeb(session);
	}

	/// <summary>
	/// Pushes a state-only undo/redo result. Disk-mutating results publish through session file activity.
	/// History goes first: the client's undo/redo availability must already reflect this action by the time
	/// the diff/changes pushes land and trigger a re-render, or a keystroke landing in that gap (undoKeep
	/// reads canUndoKeep synchronously, with no retry) silently no-ops forever. See diff-review.spec.ts's
	/// "keeping a hunk drops only it from the diff; undo brings it back".
	/// </summary>
	private void ApplyHistoryResult(HostSession session, ReviewHistoryResult result) {
		if (result.TouchedDisk) {
			return;
		}

		PushReviewHistoryToWeb(session);
		foreach (string path in result.Paths) {
			if (session.Changes.GetTurn(path) is not null) {
				PushTurnDiffToWeb(session, path);
			}
		}

		PushTurnChangesToWeb(session);
	}

	/// <summary>Pushes the review undo/redo availability so the page enables its Undo/Redo affordances.</summary>
	private static void PushReviewHistoryToWeb(HostSession session) =>
		PushReviewHistoryToWeb(session, session.Bus.BroadcastTarget);

	private static void PushReviewHistoryToWeb(
		HostSession session,
		MessageTarget target) =>
		target.Feature("review").PublishJson(
			"history",
			ChangeMessages.ReviewHistory(session.Changes));

	/// <summary>
	/// Reverts a single hunk on disk: the web sends the hunk's line ranges and a <c>guardText</c> snapshot, and
	/// Core splices its own baseline lines back in (never the message's). A guard mismatch (a parallel edit moved
	/// the file) aborts without writing and re-emits a fresh diff; reverting a created file's last hunk deletes it.
	/// </summary>
	private void RejectHunk(HostSession session, JsonElement root) {
		string path = root.TryGetProperty("path", out var pathEl) ? pathEl.GetString() ?? string.Empty : string.Empty;
		if (string.IsNullOrEmpty(path)) {
			return;
		}

		var baselineRange = new LineRange(JsonInt(root, "baselineStart"), JsonInt(root, "baselineEndExclusive"));
		var currentRange = new LineRange(JsonInt(root, "currentStart"), JsonInt(root, "currentEndExclusive"));
		string guardText = root.TryGetProperty("guardText", out var gEl) ? gEl.GetString() ?? string.Empty : string.Empty;

		try {
			var outcome = session.Changes.RevertHunk(path, baselineRange, currentRange, guardText);
			if (outcome == RevertHunkOutcome.GuardMismatch) {
				Notify(session, "warn", $"{Path.GetFileName(path)} changed — re-open to review.");
				PushTurnDiffToWeb(session, path);
				return;
			}

		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Notify(session, "warn", $"Couldn't revert {Path.GetFileName(path)}: {ex.Message}");
		}
	}

	/// <summary>
	/// Reverts one file to its review baseline on disk — the file-scoped analogue of <see cref="UndoTurn"/>,
	/// sharing <see cref="SessionChangeTracker.RevertFile"/>. Refreshes the editor and re-emits the review set so
	/// the now-clean file leaves the ← / → walk.
	/// </summary>
	private void RevertFile(HostSession session, JsonElement root) {
		string path = root.GetStringOrEmpty("path");
		if (string.IsNullOrEmpty(path)) {
			return;
		}

		try {
			session.Changes.RevertFile(path);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Notify(session, "warn", $"Couldn't revert {Path.GetFileName(path)}: {ex.Message}");
		}
	}

	/// <summary>
	/// Keeps a single hunk: advances Core's review baseline over it (no disk write) so it drops from the pending
	/// diff for good and survives session switches. The web sends the same line ranges + <c>guardText</c> as a
	/// revert; a guard mismatch (a parallel edit moved the file) re-emits a fresh diff without advancing.
	/// </summary>
	private void KeepHunk(HostSession session, JsonElement root) {
		string path = root.GetStringOrEmpty("path");
		if (string.IsNullOrEmpty(path)) {
			return;
		}

		var baselineRange = new LineRange(JsonInt(root, "baselineStart"), JsonInt(root, "baselineEndExclusive"));
		var currentRange = new LineRange(JsonInt(root, "currentStart"), JsonInt(root, "currentEndExclusive"));
		string guardText = root.GetStringOrEmpty("guardText");

		if (!session.Changes.KeepHunk(path, baselineRange, currentRange, guardText)) {
			Notify(session, "warn", $"{Path.GetFileName(path)} changed — re-open to review.");
			PushTurnDiffToWeb(session, path);
			return;
		}

		// History before diff/changes — see ApplyHistoryResult's doc comment on why the order matters.
		PushReviewHistoryToWeb(session);
		PushTurnDiffToWeb(session, path);
		PushTurnChangesToWeb(session);
	}

	/// <summary>
	/// Keeps a whole file: advances its review baseline to current (no disk write) so it leaves the review set for
	/// good — the file-scoped analogue of keep-all, sharing <see cref="SessionChangeTracker.KeepFile"/>.
	/// </summary>
	private void KeepFile(HostSession session, JsonElement root) {
		string path = root.GetStringOrEmpty("path");
		if (string.IsNullOrEmpty(path)) {
			return;
		}

		session.Changes.KeepFile(path);
		// History before diff/changes — see ApplyHistoryResult's doc comment on why the order matters.
		PushReviewHistoryToWeb(session);
		PushTurnDiffToWeb(session, path);
		PushTurnChangesToWeb(session);
	}

	/// <summary>
	/// Un-keeps a single faded (accepted) hunk: Core splices its accepted-anchor lines back into the review
	/// baseline, returning it to the bright pending band. The inverse of <see cref="KeepHunk"/>; the web sends the
	/// accepted-anchor + review-baseline ranges and both sides' guard snapshots (a mismatch — a concurrent keep
	/// moved the baseline, or a turn boundary committed the anchor — re-emits a fresh diff without un-keeping).
	/// No disk write.
	/// </summary>
	private void UnkeepHunk(HostSession session, JsonElement root) {
		string path = root.GetStringOrEmpty("path");
		if (string.IsNullOrEmpty(path)) {
			return;
		}

		var acceptedRange = new LineRange(JsonInt(root, "acceptedStart"), JsonInt(root, "acceptedEndExclusive"));
		var reviewRange = new LineRange(JsonInt(root, "reviewStart"), JsonInt(root, "reviewEndExclusive"));
		string acceptedGuardText = root.GetStringOrEmpty("acceptedGuardText");
		string guardText = root.GetStringOrEmpty("guardText");

		if (!session.Changes.UnkeepHunk(path, acceptedRange, reviewRange, acceptedGuardText, guardText)) {
			Notify(session, "warn", $"{Path.GetFileName(path)} changed — re-open to review.");
			PushTurnDiffToWeb(session, path);
			return;
		}

		PushTurnDiffToWeb(session, path);
		PushTurnChangesToWeb(session);
	}

	/// <summary>Reads a string-array property from a web message (empty when absent or not an array); skips non-string elements.</summary>
	private static List<string> StringArray(JsonElement root, string name) {
		var values = new List<string>();
		if (root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array) {
			foreach (var element in array.EnumerateArray()) {
				if (element.ValueKind == JsonValueKind.String && element.GetString() is { } value) {
					values.Add(value);
				}
			}
		}

		return values;
	}

	/// <summary>Reads a required integer property from a web message (0 when absent/non-numeric).</summary>
	private static int JsonInt(JsonElement root, string name) =>
		root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt32() : 0;

	/// <summary>
	/// True only for an absolute http/https URL — the open-url gate. Terminal content is untrusted, so the OS
	/// opener accepts nothing else: never a <c>file://</c>, a UNC path, or a custom scheme that could launch a
	/// handler.
	/// </summary>
	private static bool IsHttpUrl(string url) =>
		Uri.TryCreate(url, UriKind.Absolute, out var uri)
		&& (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

	/// <summary>Surfaces a prior run's unhandled crash as a one-time toast pointing at the saved report.</summary>
	private void SurfacePriorCrash() {
		if (CrashReporter.TakePendingReport(_lastCrashFile, _previousCrashFile) is not null) {
			// The crash is the better explanation of the same ending, so it also settles the unfinished marker
			// this run inherited — a later hello must not replace this toast with the vaguer one.
			_priorUnfinishedRun = string.Empty;
			// Keyed so two windows handling `ready` at once collapse to a single toast (matches the malformed-settings notice).
			Notify("error", $"Weavie exited unexpectedly last session. A crash report was saved to {_previousCrashFile}.", "prior-crash");
			return;
		}

		// No crash, yet the last run never stamped an ending — the fingerprint of a stop nothing could observe.
		// What ended it is exactly what isn't known, so the toast says that and points at the journal.
		if (_priorUnfinishedRun.Length > 0) {
			_priorUnfinishedRun = string.Empty;
			Notify(
				"error",
				$"Weavie's last session ended without recording how it stopped, and left no crash report. "
					+ $"Details for a bug report are in {_exitJournalFile}.",
				"prior-crash");
		}
	}

	/// <summary>Pushes a user-facing notification (rendered as a toast in the page).</summary>
	public void Notify(string level, string message) =>
		_messages.Host.Feature("notifications").Publish("show", new { level, message });

	private static void Notify(HostSession session, string level, string message) =>
		session.Bus.Feature("notifications").Publish("show", new { level, message });

	/// <summary>
	/// As <see cref="Notify(string,string)"/>, with a dedupe <paramref name="key"/>: a later toast carrying the
	/// same key replaces the live one in place (e.g. a "reloaded" info clearing a lingering "malformed" error).
	/// </summary>
	public void Notify(string level, string message, string key) =>
		_messages.Host.Feature("notifications").Publish("show", new { level, message, key });

	/// <summary>
	/// As <see cref="Notify(string,string,string)"/>, with an action backed by a registered command. The page
	/// resolves the command's effective shortcut from the owning host's catalog.
	/// </summary>
	public void Notify(
		string level,
		string message,
		string key,
		string actionLabel,
		string commandId,
		string? argsJson) =>
		_messages.Host.Feature("notifications").Publish("show", new {
			level,
			message,
			key,
			action = new { label = actionLabel, commandId, argsJson },
		});

	/// <summary>Dismisses the live toast carrying <paramref name="key"/> in the page (an in-flight spinner whose operation finished).</summary>
	public void ClearNotify(string key) =>
		_messages.Host.Feature("notifications").Publish("clear", new { key });

	private async Task<string[]> ListBranchesAsync(CancellationToken ct) {
		var git = new GitService();
		string[] branches = [];
		try {
			var all = await git.ListBranchesAsync(WorkspaceRoot, ct).ConfigureAwait(false);
			var worktrees = await git.ListWorktreesAsync(WorkspaceRoot, ct).ConfigureAwait(false);
			var sessionBranches = new HashSet<string>(
				_sessions?.Slots.Select(slot => slot.Label) ?? [],
				StringComparer.Ordinal);
			var checkedOut = new HashSet<string>(
				worktrees
					.Where(w => w.Branch is not null && !sessionBranches.Contains(w.Branch))
					.Select(w => w.Branch!),
				StringComparer.Ordinal);
			branches = [.. all.Where(b => !checkedOut.Contains(b))];
		} catch (GitException ex) {
			Log($"[weavie] list-branches failed: {ex.Message}");
		}

		return branches;
	}

	private async Task<string[]> ListRefsAsync(HostSession session, CancellationToken ct) {
		string[] refs = [];
		try {
			refs = [.. await new GitService().ListRefsAsync(session.WorkspaceRoot, ct).ConfigureAwait(false)];
		} catch (GitException ex) {
			Log($"[weavie] list-refs failed: {ex.Message}");
		}

		return refs;
	}

	private async Task<CommandResult> InvokeCommandAsync(
		HostSession session,
		string id,
		string? argsJson,
		CancellationToken ct) {
		var execution = await PrepareCommandAsync(session, id, argsJson, ct).ConfigureAwait(false);
		await execution.CompleteAsync(ct).ConfigureAwait(false);
		return execution.Result;
	}

	private async Task<CommandExecution> PrepareCommandAsync(
		HostSession session,
		string id,
		string? argsJson,
		CancellationToken ct) {
		if (string.IsNullOrEmpty(id)) {
			return CommandExecution.Completed(CommandResult.Failure("A command id is required."));
		}

		CommandExecution execution;
		try {
			execution = await session.Commands.PrepareAsync(id, argsJson, ct).ConfigureAwait(false);
		} catch (Exception ex) {
			execution = CommandExecution.Completed(CommandResult.Failure(ex.Message));
		}

		if (!execution.Result.Ok) {
			Log($"[weavie] command {id} failed: {execution.Result.Error}");
		}

		return execution;
	}

	private Task<CommandResult> InvokeWebCommandAsync(
		HostSession session,
		string id,
		string? argsJson,
		CancellationToken ct) => InvokeViewCommandAsync(session, "run", id, argsJson, ct);

	private Task<CommandResult> InvokeClientCommandAsync(
		HostSession session,
		string id,
		string? argsJson,
		CancellationToken ct) => InvokeViewCommandAsync(session, "runClient", id, argsJson, ct);

	private async Task<CommandResult> InvokeViewCommandAsync(
		HostSession session,
		string name,
		string id,
		string? argsJson,
		CancellationToken ct) {
		JsonElement? args = null;
		if (!string.IsNullOrWhiteSpace(argsJson)) {
			using var document = JsonDocument.Parse(argsJson);
			args = document.RootElement.Clone();
		}

		var result = await session.View.Feature("commands").RequestAsync<CommandRequest, CommandWireResult>(
			name,
			new CommandRequest(id, args),
			ct).ConfigureAwait(false);
		return FromWireResult(result);
	}

	/// <summary>The vsix picker for the install-from-file theme command, or <c>null</c> when the host has no native dialogs.</summary>
	private VsixFilePicker? VsixPicker =>
		_platform.Dialogs is { } dialogs ? dialogs.PickVsixFileAsync : null;

	/// <summary>Encodes a string as a JSON string literal (trim-safe; no reflection).</summary>
	private static string JsonString(string value) => "\"" + JsonEncodedText.Encode(value) + "\"";
}
