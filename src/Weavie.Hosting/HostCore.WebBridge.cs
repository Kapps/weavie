using System.Text;
using System.Text.Json;
using Weavie.Core.Changes;
using Weavie.Core.Commands;
using Weavie.Core.Editor;
using Weavie.Core.FileActivity;
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

	/// <summary>Pushes the persisted/reconciled layout document to the web app as a compact set-layout message.</summary>
	private void PushLayoutToWeb() {
		string documentJson = LayoutSerialization.SerializeCompact(_layout.Current);
		_messages.Host.Feature("layout").PublishJson("state", $"{{\"document\":{documentJson}}}");
	}

	/// <summary>Applies editor state sent through its owning session bus.</summary>
	private void HandleEditorSessionChanged(HostSession target, JsonElement sessionElement) {
		if (!EditorSessionSerialization.TryDeserialize(sessionElement.GetRawText(), out var session, out string? error)
			|| session is null) {
			Log($"[bridge] invalid editor session for {target.SlotId}: {error}");
			return;
		}

		target.EditorSession = session;
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
		if (invalidate) {
			target.Feature("files").Publish("index", new {
				root = session.FileIndex.Root,
				files = Array.Empty<string>(),
				pending = true,
			});
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
				target.Feature("files").Publish("index", new {
					root = session.FileIndex.Root,
					files = Array.Empty<string>(),
					pending = false,
				});
				Notify(session, "error", $"Couldn't load workspace files: {ex.Message}");
				return;
			}

			ct.ThrowIfCancellationRequested();
			target.Feature("files").Publish("index", new {
				root = session.FileIndex.Root,
				files,
				pending = false,
			});
		});
	}

	// How many recent files to push: enough to power the recency tiebreak across a working set, of which the
	// omnibar renders the top few as its Recent section.
	private const int RecentFilesPushCount = 50;

	/// <summary>
	/// Records a visit to the primary session's active file in the per-workspace recent-files store and re-pushes
	/// the list. Wired to the primary's <see cref="EditorStore.Changed"/> (so it's primary-only, like the persisted
	/// editor session) and deduped against the last visit so the active-editor stream — which also fires on cursor
	/// moves within a file — bumps frecency once per distinct file, not per move.
	/// </summary>
	private void RecordRecentFile(ActiveEditor editor) {
		if (string.Equals(editor.FilePath, _lastRecentPath, StringComparison.Ordinal)) {
			return;
		}

		_lastRecentPath = editor.FilePath;
		_recentFiles.Record(editor.FilePath, DateTime.UtcNow.Ticks);
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
	/// per-hunk/per-file/whole-set reverts); the host only keeps the workspace guard and the editor pushes.
	/// </summary>
	private void UndoTurn(HostSession session) {
		// Workspace-guard every file before touching disk: one path outside the worktree aborts the whole revert.
		foreach (var change in session.Changes.TurnChanges()) {
			if (!BufferStore.IsWithinWorkspace(session.WorkspaceRoot, change.Path)) {
				Notify(session, "warn", $"Couldn't revert {Path.GetFileName(change.Path)}: path is outside the workspace.");
				return;
			}
		}

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
	/// </summary>
	private void ApplyHistoryResult(HostSession session, ReviewHistoryResult result) {
		if (result.TouchedDisk) {
			return;
		}

		foreach (string path in result.Paths) {
			if (session.Changes.GetTurn(path) is not null) {
				PushTurnDiffToWeb(session, path);
			}
		}

		PushTurnChangesToWeb(session);
		PushReviewHistoryToWeb(session);
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

		if (!BufferStore.IsWithinWorkspace(session.WorkspaceRoot, path)) {
			Notify(session, "warn", $"Couldn't revert {Path.GetFileName(path)}: path is outside the workspace.");
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
	/// sharing <see cref="SessionChangeTracker.RevertFile"/>. Workspace-guards the path, refreshes the editor, and
	/// re-emits the review set so the now-clean file leaves the ← / → walk.
	/// </summary>
	private void RevertFile(HostSession session, JsonElement root) {
		string path = root.GetStringOrEmpty("path");
		if (string.IsNullOrEmpty(path)) {
			return;
		}

		if (!BufferStore.IsWithinWorkspace(session.WorkspaceRoot, path)) {
			Notify(session, "warn", $"Couldn't revert {Path.GetFileName(path)}: path is outside the workspace.");
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
		if (string.IsNullOrEmpty(path) || !BufferStore.IsWithinWorkspace(session.WorkspaceRoot, path)) {
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

		PushTurnDiffToWeb(session, path);
		PushTurnChangesToWeb(session);
		PushReviewHistoryToWeb(session);
	}

	/// <summary>
	/// Keeps a whole file: advances its review baseline to current (no disk write) so it leaves the review set for
	/// good — the file-scoped analogue of keep-all, sharing <see cref="SessionChangeTracker.KeepFile"/>.
	/// </summary>
	private void KeepFile(HostSession session, JsonElement root) {
		string path = root.GetStringOrEmpty("path");
		if (string.IsNullOrEmpty(path) || !BufferStore.IsWithinWorkspace(session.WorkspaceRoot, path)) {
			return;
		}

		session.Changes.KeepFile(path);
		PushTurnDiffToWeb(session, path);
		PushTurnChangesToWeb(session);
		PushReviewHistoryToWeb(session);
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
		if (string.IsNullOrEmpty(path) || !BufferStore.IsWithinWorkspace(session.WorkspaceRoot, path)) {
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

	/// <summary>Runs a native menu command against the primary session.</summary>
	public void InvokeCommand(string id) => InvokePrimaryCommand(id, null);

	/// <summary>Runs a native menu command with JSON arguments against the primary session.</summary>
	public void InvokeCommand(string id, string? argsJson) => InvokePrimaryCommand(id, argsJson);

	private void InvokePrimaryCommand(string id, string? argsJson) {
		if (_primarySession is { } primary) {
			_ = InvokeCommandAsync(primary, id, argsJson, CancellationToken.None);
		}
	}

	/// <summary>Surfaces a prior run's unhandled crash as a one-time toast pointing at the saved report.</summary>
	private void SurfacePriorCrash() {
		if (CrashReporter.TakePendingReport() is null) {
			return;
		}

		// Keyed so two windows handling `ready` at once collapse to a single toast (matches the malformed-settings notice).
		Notify("error", $"Weavie exited unexpectedly last session. A crash report was saved to {Weavie.Core.WeaviePaths.PreviousCrashFile}.", "prior-crash");
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
				_sessions?.Slots.Where(slot => !slot.IsPrimary).Select(slot => slot.Id) ?? [],
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

	private async Task<CommandResult> InvokeWebCommandAsync(
		HostSession session,
		string id,
		string? argsJson,
		CancellationToken ct) {
		JsonElement? args = null;
		if (!string.IsNullOrWhiteSpace(argsJson)) {
			using var document = JsonDocument.Parse(argsJson);
			args = document.RootElement.Clone();
		}

		var result = await session.View.Feature("commands").RequestAsync<CommandRequest, CommandWireResult>(
			"run",
			new CommandRequest(id, args),
			ct).ConfigureAwait(false);
		return FromWireResult(result);
	}

	private async Task<CommandResult> InvokeClientCommandAsync(
		HostSession session,
		string id,
		string? argsJson,
		CancellationToken ct) {
		JsonElement? args = null;
		if (!string.IsNullOrWhiteSpace(argsJson)) {
			using var document = JsonDocument.Parse(argsJson);
			args = document.RootElement.Clone();
		}

		var result = await session.View.Feature("commands").RequestAsync<CommandRequest, CommandWireResult>(
			"runClient",
			new CommandRequest(id, args),
			ct).ConfigureAwait(false);
		return FromWireResult(result);
	}

	/// <summary>The vsix picker for the install-from-file theme command, or <c>null</c> when the host has no native dialogs.</summary>
	private VsixFilePicker? VsixPicker =>
		_platform.Dialogs is { } dialogs ? dialogs.PickVsixFileAsync : null;

	/// <summary>
	/// Saves a scratch (untitled) buffer under a real name via the native Save-As dialog, deletes the temp, and
	/// replies <c>scratch-saved</c>. <c>reopen</c> is true only for an in-workspace target (the editor can't edit
	/// out-of-workspace files); replies cancelled when the host has no native dialog.
	/// </summary>
	private async Task<ScratchSaveResult> SaveScratchAsAsync(
		HostSession session,
		JsonElement root,
		CancellationToken ct) {
		string scratchPath = root.TryGetProperty("path", out var pEl) ? pEl.GetString() ?? string.Empty : string.Empty;
		string content = root.TryGetProperty("content", out var cEl) ? cEl.GetString() ?? string.Empty : string.Empty;
		string suggested = root.TryGetProperty("suggestedName", out var nEl) ? nEl.GetString() ?? "Untitled" : "Untitled";

		try {
			// Default the dialog to the owning session's worktree, so saving from a worktree session lands there
			// and the reopen check below recognizes it as in-workspace.
			string sessionRoot = Path.GetFullPath(session.WorkspaceRoot);
			string? target = _platform.Dialogs is { } dialogs
				? await dialogs.PickSaveAsPathAsync(suggested, sessionRoot, ct).ConfigureAwait(false)
				: null;

			if (string.IsNullOrEmpty(target)) {
				return new ScratchSaveResult(scratchPath, string.Empty, false);
			}

			try {
				session.FileSystem.WriteAllText(target, content);
			} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
				Notify(session, "error", $"Couldn't save {Path.GetFileName(target)}: {ex.Message}");
				return new ScratchSaveResult(scratchPath, string.Empty, false);
			}

			bool reopen = BufferStore.IsWithinWorkspace(session.WorkspaceRoot, target);
			if (reopen && session.FileSystem.TryGetStat(target, out var revision)) {
				session.FileActivity.ReportChanged(target, revision);
			}
			session.Scratch.Delete(scratchPath);
			if (!reopen) {
				Notify(session, "info", $"Saved {Path.GetFileName(target)} outside the workspace — it won't open in the editor.");
			}

			return new ScratchSaveResult(scratchPath, target, reopen);
		} catch (Exception ex) {
			Notify(session, "error", $"Couldn't save the file: {ex.Message}");
			return new ScratchSaveResult(scratchPath, string.Empty, false);
		}
	}

	/// <summary>
	/// Saves a scratch buffer under an in-app-chosen workspace-relative <c>name</c> (browser-served host, no
	/// native dialog), resolved under the owning session's worktree, then deletes the temp and replies
	/// <c>scratch-saved</c>. Rejects a name that escapes the workspace.
	/// </summary>
	private ScratchSaveResult SaveScratchNamed(HostSession session, JsonElement root) {
		string scratchPath = root.GetStringOrEmpty("path");
		string name = root.GetStringOrEmpty("name").Trim();
		if (name.Length == 0) {
			return new ScratchSaveResult(scratchPath, string.Empty, false);
		}

		string content = root.GetStringOrEmpty("content");
		string target = Path.GetFullPath(Path.Combine(Path.GetFullPath(session.WorkspaceRoot), name));
		if (!BufferStore.IsWithinWorkspace(session.WorkspaceRoot, target)) {
			Notify(session, "error", $"Can't save outside the workspace: {name}");
			return new ScratchSaveResult(scratchPath, string.Empty, false);
		}

		try {
			session.FileSystem.WriteAllText(target, content);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Notify(session, "error", $"Couldn't save {Path.GetFileName(target)}: {ex.Message}");
			return new ScratchSaveResult(scratchPath, string.Empty, false);
		}

		if (session.FileSystem.TryGetStat(target, out var revision)) {
			session.FileActivity.ReportChanged(target, revision);
		}
		session.Scratch.Delete(scratchPath);
		return new ScratchSaveResult(scratchPath, target, true);
	}

	private sealed record ScratchSaveResult(string ScratchPath, string SavedPath, bool Reopen);

	/// <summary>Encodes a string as a JSON string literal (trim-safe; no reflection).</summary>
	private static string JsonString(string value) => "\"" + JsonEncodedText.Encode(value) + "\"";
}
