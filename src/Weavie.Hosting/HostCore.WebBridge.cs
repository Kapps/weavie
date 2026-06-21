using System.Text.Json;
using Weavie.Core.Changes;
using Weavie.Core.Commands;
using Weavie.Core.Editor;
using Weavie.Core.Git;
using Weavie.Core.Layout;
using Weavie.Core.Lsp;
using Weavie.Core.Sessions;
using Weavie.Core.Shell;
using Weavie.Core.Theming;
using Weavie.Core.Workspaces;

namespace Weavie.Hosting;

// The web <-> host message bridge: the inbound OnWebMessage dispatcher and every outbound Push*ToWeb /
// command / turn / scratch helper. All of it routes through the ACTIVE session (_session) and the bridge, so
// it's identical on every host — the platform only supplies the bridge, the UI marshal, and the optional
// native dialogs.
public sealed partial class HostCore {
	private void OnWebMessage(string json) {
		string type;
		JsonElement root;
		try {
			using var doc = JsonDocument.Parse(json);
			root = doc.RootElement.Clone();
			type = root.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty;
		} catch (JsonException) {
			Log($"[weavie] (unparsed) {json}");
			return;
		}

		switch (type) {
			case "term-input":
				TerminalFor(root)?.Write(Convert.FromBase64String(root.GetProperty("dataB64").GetString() ?? string.Empty));
				break;
			case "term-resize":
				TerminalFor(root)?.Resize(root.GetProperty("cols").GetInt32(), root.GetProperty("rows").GetInt32());
				break;
			case "term-ready":
				TerminalFor(root)?.OnReady(root.GetProperty("cols").GetInt32(), root.GetProperty("rows").GetInt32());
				break;
			case "switch-session": {
				string switchId = root.TryGetProperty("id", out var ssEl) ? ssEl.GetString() ?? string.Empty : string.Empty;
				if (!string.IsNullOrEmpty(switchId) && _sessions?.Find(switchId) is { } target) {
					SwitchToSlot(target);
				}

				break;
			}

			case "new-session": {
				string? branch = root.TryGetProperty("branch", out var nsEl) ? nsEl.GetString() : null;
				// "existing" ⇒ check out the named branch into a new worktree rather than creating a new branch.
				bool existing = root.TryGetProperty("existing", out var exEl)
					&& exEl.ValueKind is JsonValueKind.True or JsonValueKind.False && exEl.GetBoolean();
				// The page sends base "head" (the active session's HEAD) or "main"; normalize to "current"/"main"
				// (ResolveBaseRefAsync treats anything but "main" as the current session's HEAD). Ignored for an
				// existing-branch checkout (the branch already has a tip).
				string? baseSpec = root.TryGetProperty("base", out var nbEl) ? nbEl.GetString() : null;
				string? resolvedBase = baseSpec is null
					? null
					: string.Equals(baseSpec, "main", StringComparison.OrdinalIgnoreCase) ? "main" : "current";
				_ = CreateSessionFromWebAsync(branch, resolvedBase, existing);
				break;
			}

			case "list-branches": {
				string branchesReqId = root.TryGetProperty("id", out var lbEl) ? lbEl.GetString() ?? string.Empty : string.Empty;
				_ = ListBranchesForWebAsync(branchesReqId);
				break;
			}

			case "delete-session-request": {
				string reqId = root.TryGetProperty("id", out var reqEl) ? reqEl.GetString() ?? string.Empty : string.Empty;
				_ = DeleteSessionPromptAsync(reqId);
				break;
			}

			case "delete-session": {
				string deleteId = root.TryGetProperty("id", out var delIdEl) ? delIdEl.GetString() ?? string.Empty : string.Empty;
				bool deleteForce = root.TryGetProperty("force", out var delForceEl)
					&& delForceEl.ValueKind is JsonValueKind.True or JsonValueKind.False && delForceEl.GetBoolean();
				_ = DeleteSessionFromWebAsync(deleteId, deleteForce);
				break;
			}

			case "diff-resolved":
				string diffId = root.GetProperty("id").GetString() ?? string.Empty;
				bool kept = root.TryGetProperty("kept", out var keptEl) && keptEl.GetBoolean();
				string? finalContents = root.TryGetProperty("finalContents", out var fcEl) ? fcEl.GetString() : null;
				_session?.DiffPresenter.Resolve(diffId, kept, finalContents);
				break;
			case "reveal-file":
				string revealPath = root.GetProperty("path").GetString() ?? string.Empty;
				int revealLine = root.TryGetProperty("line", out var lnEl) ? lnEl.GetInt32() : 1;
				bool revealPreview = root.TryGetProperty("preview", out var pvEl)
					&& pvEl.ValueKind is JsonValueKind.True or JsonValueKind.False && pvEl.GetBoolean();
				_session?.FileOpener.Open(revealPath, revealLine, preview: revealPreview, scratch: false);
				break;
			case "list-dir":
				_session?.ListDirectory(root.TryGetProperty("path", out var dirEl) ? dirEl.GetString() ?? string.Empty : string.Empty);
				break;
			case "active-editor-changed":
				_session?.UpdateActiveEditor(root);
				break;
			case "open-editors-changed":
				_session?.UpdateOpenEditors(root);
				break;
			case "get-turn-diff":
				PushTurnDiffToWeb(root.GetProperty("path").GetString() ?? string.Empty);
				break;
			case "fs-stat":
				if (_session is not null) {
					_bridge.PostToWeb(_session.FileProvider.Stat(FsId(root), FsPath(root)));
				}

				break;
			case "fs-read":
				if (_session is not null) {
					_bridge.PostToWeb(_session.FileProvider.Read(FsId(root), FsPath(root)));
				}

				break;
			case "fs-write":
				if (_session is not null) {
					_bridge.PostToWeb(_session.FileProvider.Write(
						FsId(root), FsPath(root),
						root.TryGetProperty("content", out var fsContentEl) ? fsContentEl.GetString() ?? string.Empty : string.Empty));
				}

				break;
			case "accept-turn":
				AcceptTurn();
				break;
			case "undo-turn":
				UndoTurn();
				break;
			case "reject-hunk":
				RejectHunk(root);
				break;
			case "invoke-command":
				// A keybinding/palette in the web invoked a Core command — run it on the active session
				// (fire-and-forget; the web doesn't await a result for its own triggers).
				InvokeCommandFromWeb(
					root.TryGetProperty("id", out var ciEl) ? ciEl.GetString() ?? string.Empty : string.Empty,
					root.TryGetProperty("args", out var caEl) && caEl.ValueKind == JsonValueKind.Object ? caEl.GetRawText() : null);
				break;
			case "command-ack":
				// The web finished a run-command (a web command Claude invoked over MCP) — settle the await.
				CompleteWebCommand(root);
				break;
			case "window-control":
				_shell?.HandleWindowControl(root);
				break;
			case "window-resize":
				_shell?.HandleWindowResize(root);
				break;
			case "menu-action":
				_shell?.HandleMenuAction(root);
				break;
			case "request-file-index":
				// Build the omnibar's quick-open index from the ACTIVE session's worktree, so switching
				// sessions re-roots "Go to File" to the files that session can actually open.
				PushFileIndexToWeb();
				break;
			case "ready":
				// The page's bridge listener is live; push the persisted layout, editor session, and session
				// list so the page restores its state. These must go on `ready`, NOT during init — PostToWeb
				// before navigation no-ops (window.__weavieReceive doesn't exist yet). Then let the shell push
				// the initial native window state (which only it knows).
				PushLayoutToWeb();
				PushEditorSessionToWeb();
				PushSessionList();
				Ready?.Invoke();
				Log($"[weavie] {json}");
				break;
			case "layout-changed":
				HandleLayoutChanged(root);
				break;
			case "editor-session-changed":
				HandleEditorSessionChanged(root);
				break;
			case "new-scratch":
				// New File (Ctrl+N): create an untitled buffer + open it as a scratch tab.
				_session?.OpenNewScratch();
				break;
			case "save-scratch-as":
				// Ctrl+S on a scratch buffer: prompt for a real name (native Save dialog), write it, drop the temp.
				_ = SaveScratchAsAsync(root);
				break;
			case "discard-scratch":
				// The user closed (and confirmed discarding) a scratch buffer: delete its temp file.
				_session?.Scratch.Delete(root.TryGetProperty("path", out var dsEl) ? dsEl.GetString() ?? string.Empty : string.Empty);
				break;
			default:
				Log($"[weavie] {json}");
				break;
		}
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

	/// <summary>Pushes the persisted/reconciled layout document to the web app as a compact set-layout message.</summary>
	private void PushLayoutToWeb() {
		string documentJson = LayoutSerialization.SerializeCompact(_layout.Current);
		_bridge.PostToWeb($"{{\"type\":\"set-layout\",\"document\":{documentJson}}}");
	}

	/// <summary>Applies an editor session the web sent (open files + view state); records it on the active session, persisting the primary's.</summary>
	private void HandleEditorSessionChanged(JsonElement root) {
		if (!root.TryGetProperty("session", out var sessionElement)) {
			return;
		}

		if (!EditorSessionSerialization.TryDeserialize(sessionElement.GetRawText(), out var session, out string? error)
			|| session is null) {
			Log($"[weavie] editor-session-changed: bad session ({error})");
			return;
		}

		if (_session is not { } active) {
			return;
		}

		// Reject a stale cross-session write. The page stamps each editor-session-changed with the id of the
		// session that owned the tab set when it was produced. A debounced change from a session the user has
		// since switched away from can still land after _session has flipped — its closure captured the previous
		// session's tabs. Without this guard those worktree paths would be misattributed to (and, for the
		// primary, persisted under) the active session, then fail to restore next launch as a blank editor
		// (out-of-root). An id-less message (older page) is accepted for back-compat.
		if (root.TryGetProperty("sessionId", out var idElement) && idElement.ValueKind == JsonValueKind.String) {
			string? owner = idElement.GetString();
			if (!string.IsNullOrEmpty(owner) && !string.Equals(owner, active.Id, StringComparison.Ordinal)) {
				Log($"[weavie] editor-session-changed ignored: from session '{owner}', active is '{active.Id}'");
				return;
			}
		}

		// Record on the active session so a switch can rebind the editor to its worktree's tabs. The primary
		// also mirrors to the persisted per-workspace store (for launch restore); secondary worktree sessions
		// are in-memory only.
		active.EditorSession = session;
		if (ReferenceEquals(active, _primarySession)) {
			_editorSession.Update(session);
		}
	}

	/// <summary>Pushes the persisted editor session (open file paths + view state) for launch restore, scoped to
	/// the primary session's root and stamped with its id so a later change can't be misattributed.</summary>
	private void PushEditorSessionToWeb() {
		if (_primarySession is not { } primary) {
			return;
		}

		_bridge.PostToWeb(EditorSessionStore.BuildRestoreJson(
			_editorSession.Current, primary.FileSystem, primary.WorkspaceRoot, primary.Id, Log));
	}

	/// <summary>
	/// Re-points the page's language clients at a session's own LSP bridge on a switch. Each session has its own
	/// bridge (own port/token, rooted at its worktree); the launch config (<c>window.__WEAVIE_LSP__</c>) is the
	/// primary's, so without this a switched-in worktree session's hover/diagnostics/go-to-def would keep
	/// resolving against the primary's checkout. The web's <c>rebindLanguageServices</c> reconnects here.
	/// </summary>
	private void PushLspConfigToWeb(HostSession session) =>
		_bridge.PostToWeb($"{{\"type\":\"lsp-config\",\"config\":{session.LspConfigJson}}}");

	/// <summary>
	/// Re-walks the ACTIVE session's worktree and pushes its <c>file-index</c> so the omnibar's "Go to File"
	/// and the file browser re-root to the active session. Runs the walk off the UI thread, and drops the
	/// result if the user switched again before it finished, so a slow walk from a stale session can't clobber
	/// the page's index.
	/// </summary>
	private void PushFileIndexToWeb() {
		if (_session is not { } session) {
			return;
		}

		_ = Task.Run(() => {
			var files = session.FileIndex.List(WorkspaceFileIndex.DefaultCap);
			if (ReferenceEquals(_session, session)) {
				_bridge.PostToWeb(ShellProtocol.BuildFileIndex(session.FileIndex.Root, files));
			}
		});
	}

	/// <summary>
	/// Pushes the per-turn change list (each file changed this turn + its first-change line) for the page's
	/// review navigator, with the host-decided auto-open flag (see <see cref="ShouldOpenReview"/>). Only in an
	/// auto-keep mode (acceptEdits/bypass), where post-turn review is the surface — default mode reviews each
	/// edit via the blocking openDiff, so there's nothing to list.
	/// </summary>
	private void PushTurnChangesToWeb() {
		if (_session is { } session && session.ObservedMode.AutoAppliesEdits) {
			_bridge.PostToWeb(ChangeMessages.TurnChanges(session.Changes, ShouldOpenReview(session)));
		}
	}

	/// <summary>
	/// Decides — and records — whether the page should auto-open the first review file: true when the active
	/// session is idle with auto-applied changes it hasn't opened yet. Keyed on the session + its first changed
	/// file and armed once, so a trickle of more edits within the same idle doesn't re-jump the editor, while a
	/// new turn (the key resets when the session leaves idle) or a switch into another session re-arms.
	/// Centralizing the decision here, where status and change set are read together, keeps it race-free.
	/// </summary>
	private bool ShouldOpenReview(HostSession session) {
		// "Done editing" = the turn ended (Idle) OR Claude is waiting on input (NeedsInput, the Notification hook
		// — the post-turn recap/prompt state). Both mean the turn's edits are settled and ready to review; only
		// an actively-Working session has nothing to show yet.
		if (session.Status.Status is not (SessionStatus.Idle or SessionStatus.NeedsInput)) {
			return false;
		}

		var turn = session.Changes.TurnChanges();
		if (turn.Count == 0) {
			return false;
		}

		// turn[0] is what the page opens as reviewFiles[0]; key the arm on it so a changed first file re-arms.
		string key = $"{session.Id}:{turn[0].Path}";
		if (string.Equals(_armedReviewKey, key, StringComparison.Ordinal)) {
			return false;
		}

		_armedReviewKey = key;
		return true;
	}

	/// <summary>
	/// Re-projects the active session's inline turn-review onto the page after a session switch. The change
	/// tracker records edits in every permission mode, but the live turn pushes are gated on
	/// <c>IsActiveSession</c> — so a session that edited while muted has a populated tracker the page never heard
	/// about, and the previous session's ← / → walk is still showing. Clears the outgoing session's inline
	/// markers, then pushes the incoming session's review set — the real set in an auto-keep mode, an empty set
	/// otherwise (which also clears the stale walk). The arm key resets first so the incoming session re-opens
	/// its review on switch-in.
	/// </summary>
	private void PushReviewStateOnSwitch() {
		if (_session is not { } session) {
			return;
		}

		_armedReviewKey = null;
		_bridge.PostToWeb(ChangeMessages.TurnReset());
		_bridge.PostToWeb(session.ObservedMode.AutoAppliesEdits
			? ChangeMessages.TurnChanges(session.Changes, ShouldOpenReview(session))
			: ChangeMessages.EmptyTurnChanges());
	}

	/// <summary>
	/// Pushes a live-refresh of one edited file via an <c>fs-change</c> (the provider fires its change event →
	/// VSCode reloads the non-dirty model from disk).
	/// </summary>
	private void PushRefreshToWeb(string path) {
		if (_session?.Changes.Get(path) is { } change) {
			_bridge.PostToWeb(FileProviderProtocol.Changed(change.Path, "updated"));
		}
	}

	/// <summary>
	/// Pushes an <c>fs-change</c> removal for a file deleted mid-turn (the change tracker reconciled it off disk),
	/// so the page closes its tab and clears the inline marker — the vanished-file counterpart to
	/// <see cref="PushRefreshToWeb"/>. Reaches files the workspace watcher doesn't (it filters by extension), which
	/// is how a created-then-deleted scratch file would otherwise strand the ← / → review walk on a dead path.
	/// </summary>
	private void PushDeletionToWeb(string path) =>
		_bridge.PostToWeb(FileProviderProtocol.Changed(path, "deleted"));

	/// <summary>Forwards a workspace-watcher batch (non-Claude on-disk edits) to the page's <c>file://</c> provider.</summary>
	private void PushWatcherChangesToWeb(IReadOnlyList<WatchedFileChange> changes) {
		if (FileProviderProtocol.WatchedChanges(changes) is { } json) {
			_bridge.PostToWeb(json);
		}
	}

	/// <summary>
	/// Pushes one file's per-turn diff so the page renders it inline. Only in an auto-keep mode
	/// (acceptEdits/bypass), where the applied turn-markers are the review surface; in default mode openDiff is
	/// the per-edit review, so a second marker would demand a redundant Accept — suppress it.
	/// </summary>
	private void PushTurnDiffToWeb(string path) {
		if (_session is { } session && session.ObservedMode.AutoAppliesEdits
			&& session.Changes.GetTurn(path) is { } turn) {
			_bridge.PostToWeb(ChangeMessages.TurnDiff(turn));
		}
	}

	/// <summary>
	/// Keep-all: advances every tracked file's review baseline to current, clearing the page's inline markers and
	/// pushing the now-empty review set so the ← / → file walk empties too (the debt-clearing action).
	/// </summary>
	private void AcceptTurn() {
		if (_session is null) {
			return;
		}

		_session.Changes.AcceptTurn();
		_bridge.PostToWeb(ChangeMessages.TurnReset());
		PushTurnChangesToWeb();
	}

	/// <summary>
	/// Undoes the whole review set: reverts every changed file to its review baseline on disk and live-refreshes
	/// the editor. The delete-vs-truncate decision (a file created since its baseline is DELETED, not truncated)
	/// lives in <see cref="SessionChangeTracker.RevertFile"/>, so per-hunk, per-file, and whole-set reverts all
	/// behave identically; the host only keeps the workspace guard and the editor pushes.
	/// </summary>
	private void UndoTurn() {
		if (_session is not { } session) {
			return;
		}

		foreach (var change in session.Changes.TurnChanges()) {
			if (!BufferStore.IsWithinWorkspace(session.WorkspaceRoot, change.Path)) {
				Notify("error", $"Couldn't undo {Path.GetFileName(change.Path)}: path is outside the workspace.");
				continue;
			}

			try {
				PushAfterRevert(change.Path, session.Changes.RevertFile(change.Path));
			} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
				Notify("error", $"Couldn't undo {Path.GetFileName(change.Path)}: {ex.Message}");
			}
		}

		PushTurnChangesToWeb();
	}

	/// <summary>
	/// Reverts a single hunk on disk: the web sends the hunk's baseline + current line ranges and a
	/// <c>guardText</c> snapshot of the current lines, and Core splices the baseline lines back in — sourcing the
	/// replacement from its own baseline, never from the message. A guard mismatch (a parallel agent / later edit
	/// moved the file) aborts without writing and re-emits a fresh diff; reverting a created file's last hunk
	/// deletes it. On success the per-file diff and the review set are re-emitted so the inline diff drops the
	/// reverted hunk.
	/// </summary>
	private void RejectHunk(JsonElement root) {
		if (_session is not { } session) {
			return;
		}

		string path = root.TryGetProperty("path", out var pathEl) ? pathEl.GetString() ?? string.Empty : string.Empty;
		if (string.IsNullOrEmpty(path)) {
			return;
		}

		if (!BufferStore.IsWithinWorkspace(session.WorkspaceRoot, path)) {
			Notify("error", $"Couldn't revert {Path.GetFileName(path)}: path is outside the workspace.");
			return;
		}

		var baselineRange = new LineRange(JsonInt(root, "baselineStart"), JsonInt(root, "baselineEndExclusive"));
		var currentRange = new LineRange(JsonInt(root, "currentStart"), JsonInt(root, "currentEndExclusive"));
		string guardText = root.TryGetProperty("guardText", out var gEl) ? gEl.GetString() ?? string.Empty : string.Empty;

		try {
			var outcome = session.Changes.RevertHunk(path, baselineRange, currentRange, guardText);
			if (outcome == RevertHunkOutcome.GuardMismatch) {
				Notify("warn", $"{Path.GetFileName(path)} changed — re-open to review.");
				PushTurnDiffToWeb(path); // re-render so the stale hunk geometry is replaced
				return;
			}

			PushAfterRevert(path, outcome);
			PushTurnChangesToWeb(); // the file may have left the review set
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Notify("error", $"Couldn't revert {Path.GetFileName(path)}: {ex.Message}");
		}
	}

	/// <summary>
	/// Pushes the editor refresh for a completed revert (per-hunk or whole-file): an <c>fs-change</c> removal for
	/// a deleted file (the editor closes its tab), else a reload plus a fresh per-file diff so the reverted
	/// markers drop.
	/// </summary>
	private void PushAfterRevert(string path, RevertHunkOutcome outcome) {
		if (outcome == RevertHunkOutcome.Deleted) {
			_bridge.PostToWeb(FileProviderProtocol.Changed(path, "deleted"));
		} else {
			PushRefreshToWeb(path);  // fs-change → the editor reloads the rewritten file
			PushTurnDiffToWeb(path); // the inline diff drops the reverted hunk(s)
		}
	}

	/// <summary>Reads a required integer property from a web message (0 when absent/non-numeric).</summary>
	private static int JsonInt(JsonElement root, string name) =>
		root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt32() : 0;

	/// <summary>
	/// Runs a Core command on the active session from a native trigger (e.g. the macOS menu bar), the same path
	/// the web's <c>invoke-command</c> takes. Fire-and-forget; failures are logged.
	/// </summary>
	public void InvokeCommand(string id) => InvokeCommandFromWeb(id, null);

	/// <summary>Runs a Core command with JSON arguments on the active session (native-trigger overload).</summary>
	public void InvokeCommand(string id, string? argsJson) => InvokeCommandFromWeb(id, argsJson);

	/// <summary>Pushes a user-facing notification (rendered as a toast in the page).</summary>
	public void Notify(string level, string message) =>
		_bridge.PostToWeb($"{{\"type\":\"notify\",\"level\":{JsonString(level)},\"message\":{JsonString(message)}}}");

	/// <summary>
	/// Creates a session from the page's <c>new-session</c> request and surfaces any failure as a toast. The
	/// rail's "+" is fire-and-forget, so without this a "branch already exists" error would only reach the log.
	/// </summary>
	private async Task CreateSessionFromWebAsync(string? branch, string? baseSpec, bool attachExisting) {
		var result = await NewSessionAsync(
			new NewSessionRequest { Branch = branch, Base = baseSpec, AttachExisting = attachExisting }, CancellationToken.None).ConfigureAwait(false);
		if (!result.Ok) {
			Notify("error", result.Error ?? "Couldn't create the session.");
		}
	}

	/// <summary>
	/// Answers the new-session dialog's branch typeahead: the local branches that can be checked out as a new
	/// session — every local branch minus those already checked out in a worktree, which git won't let a second
	/// worktree attach. Replies with a <c>branches-result</c> tagged by <paramref name="id"/>; a non-repo or git
	/// failure yields an empty list.
	/// </summary>
	private async Task ListBranchesForWebAsync(string id) {
		var git = new GitService();
		string[] branches = [];
		try {
			var all = await git.ListBranchesAsync(WorkspaceRoot, CancellationToken.None).ConfigureAwait(false);
			var worktrees = await git.ListWorktreesAsync(WorkspaceRoot, CancellationToken.None).ConfigureAwait(false);
			var checkedOut = new HashSet<string>(
				worktrees.Where(w => w.Branch is not null).Select(w => w.Branch!), StringComparer.Ordinal);
			branches = [.. all.Where(b => !checkedOut.Contains(b))];
		} catch (GitException ex) {
			Log($"[weavie] list-branches failed: {ex.Message}");
		}

		_bridge.PostToWeb(JsonSerializer.Serialize(new { type = "branches-result", id, branches }));
	}

	/// <summary>
	/// Handles the rail's <c>delete-session-request</c>: classifies the worktree's working-tree state (clean /
	/// untracked-only / modified) and asks the page to raise the matching confirm, so the user sees the right
	/// warning up front — never the reassuring "branch is kept" message when uncommitted work would be lost.
	/// </summary>
	private async Task DeleteSessionPromptAsync(string id) {
		if (string.IsNullOrEmpty(id)) {
			return;
		}

		var slot = _sessions?.Find(id);
		if (slot is null) {
			Notify("error", "No such session.");
			return;
		}

		// A worktree that's gone or half-removed (no .git) can't be inspected and has nothing left to lose —
		// prompt as clean so the user can still complete the delete, which just reconciles the leftover
		// bookkeeping.
		if (!IsLiveWorktree(slot.WorktreePath)) {
			_bridge.PostToWeb(JsonSerializer.Serialize(new { type = "session-delete-prompt", id = slot.Id, label = slot.Label, state = "clean" }));
			return;
		}

		try {
			var state = await new GitService().GetChangeStateAsync(slot.WorktreePath, CancellationToken.None).ConfigureAwait(false);
			string stateName = state switch {
				WorktreeChangeState.UntrackedOnly => "untracked",
				WorktreeChangeState.Modified => "modified",
				_ => "clean",
			};
			_bridge.PostToWeb(JsonSerializer.Serialize(new { type = "session-delete-prompt", id = slot.Id, label = slot.Label, state = stateName }));
		} catch (GitException ex) {
			Notify("error", $"Couldn't check '{slot.Label}' for changes: {ex.Message}");
		}
	}

	/// <summary>
	/// Handles the page's confirmed <c>delete-session</c>. <paramref name="force"/> is set for a dirty
	/// worktree, so the removal isn't refused. Surfaces the outcome as a toast (fire-and-forget from the page).
	/// </summary>
	private async Task DeleteSessionFromWebAsync(string id, bool force) {
		if (string.IsNullOrEmpty(id)) {
			return;
		}

		string label = _sessions?.Find(id)?.Label ?? id;
		var result = await DeleteSessionAsync(id, force, CancellationToken.None).ConfigureAwait(false);
		if (result.Ok) {
			Notify("info", $"Deleted session '{label}' (branch kept).");
		} else {
			Notify("error", result.Error ?? "Couldn't delete the session.");
		}
	}

	/// <summary>Runs a Core command the web asked for on the active session. Fire-and-forget; failures are logged.</summary>
	private void InvokeCommandFromWeb(string id, string? argsJson) {
		if (_session is null || string.IsNullOrEmpty(id)) {
			return;
		}

		_ = RunCommandSafeAsync(id, argsJson);
	}

	private async Task RunCommandSafeAsync(string id, string? argsJson) {
		if (_session is null) {
			return;
		}

		try {
			var result = await _session.Commands.InvokeAsync(id, argsJson, CancellationToken.None).ConfigureAwait(false);
			if (!result.Ok) {
				Log($"[weavie] invoke-command {id} failed: {result.Error}");
			}
		} catch (Exception ex) when (ex is UnknownCommandException or InvalidOperationException) {
			Log($"[weavie] invoke-command {id} error: {ex.Message}");
		}
	}

	/// <summary>
	/// The dispatcher's web invoker: posts a <c>run-command</c> to the page and awaits its <c>command-ack</c>
	/// (or a 5s timeout). How Claude's <c>runCommand</c> of a web command reaches the UI and returns a result.
	/// </summary>
	private async Task<CommandResult> InvokeWebCommandAsync(string id, string? argsJson, CancellationToken ct) {
		string token = Guid.NewGuid().ToString("n");
		var completion = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		_pendingWebCommands[token] = completion;
		try {
			string argsPart = string.IsNullOrEmpty(argsJson) ? "null" : argsJson;
			_bridge.PostToWeb(
				$"{{\"type\":\"run-command\",\"id\":{JsonString(id)},\"args\":{argsPart},\"token\":{JsonString(token)}}}");

			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
			timeout.CancelAfter(TimeSpan.FromSeconds(5));
			await using (timeout.Token.Register(() => completion.TrySetResult(
				CommandResult.Failure($"Command '{id}' was dispatched but the UI didn't acknowledge within 5s."))).ConfigureAwait(false)) {
				return await completion.Task.ConfigureAwait(false);
			}
		} finally {
			_pendingWebCommands.TryRemove(token, out _);
		}
	}

	/// <summary>Settles the pending web-command await for a <c>command-ack</c> message (by token).</summary>
	private void CompleteWebCommand(JsonElement root) {
		string? token = root.TryGetProperty("token", out var tokenEl) ? tokenEl.GetString() : null;
		if (string.IsNullOrEmpty(token) || !_pendingWebCommands.TryGetValue(token, out var completion)) {
			return;
		}

		bool ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind is JsonValueKind.True or JsonValueKind.False && okEl.GetBoolean();
		string? error = root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String ? errEl.GetString() : null;
		completion.TrySetResult(ok ? CommandResult.Success() : CommandResult.Failure(error ?? "The command failed in the UI."));
	}

	/// <summary>The vsix picker for the install-from-file theme command, or <c>null</c> when the host has no native dialogs.</summary>
	private VsixFilePicker? VsixPicker =>
		_platform.Dialogs is { } dialogs ? dialogs.PickVsixFileAsync : null;

	/// <summary>
	/// Saves a scratch (untitled) buffer under a real name via the native Save-As dialog: writes its content
	/// there, deletes the temp, and replies <c>scratch-saved</c>. <c>reopen</c> is true only when the target is
	/// inside the workspace (the editor can't edit out-of-workspace files). Replies cancelled when the host has
	/// no native dialog.
	/// </summary>
	private async Task SaveScratchAsAsync(JsonElement root) {
		if (_session is not { } session) {
			return;
		}

		string scratchPath = root.TryGetProperty("path", out var pEl) ? pEl.GetString() ?? string.Empty : string.Empty;
		string content = root.TryGetProperty("content", out var cEl) ? cEl.GetString() ?? string.Empty : string.Empty;
		string suggested = root.TryGetProperty("suggestedName", out var nEl) ? nEl.GetString() ?? "Untitled" : "Untitled";

		// Default the dialog to the ACTIVE session's worktree, so saving from a worktree session lands there
		// and the reopen check below recognizes it as in-workspace.
		string sessionRoot = Path.GetFullPath(session.WorkspaceRoot);
		string? target = _platform.Dialogs is { } dialogs
			? await dialogs.PickSaveAsPathAsync(suggested, sessionRoot, CancellationToken.None).ConfigureAwait(false)
			: null;

		if (string.IsNullOrEmpty(target)) {
			PostScratchSaved(scratchPath, string.Empty, reopen: false); // cancelled / no dialog
			return;
		}

		try {
			session.FileSystem.WriteAllText(target, content);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Notify("error", $"Couldn't save {Path.GetFileName(target)}: {ex.Message}");
			PostScratchSaved(scratchPath, string.Empty, reopen: false);
			return;
		}

		session.Scratch.Delete(scratchPath);
		bool reopen = BufferStore.IsWithinWorkspace(session.WorkspaceRoot, target);
		if (!reopen) {
			Notify("info", $"Saved {Path.GetFileName(target)} outside the workspace — it won't open in the editor.");
		}

		PostScratchSaved(scratchPath, target, reopen);
	}

	/// <summary>Replies to <c>save-scratch-as</c>: the saved path (empty when cancelled) + whether to reopen it.</summary>
	private void PostScratchSaved(string scratchPath, string savedPath, bool reopen) =>
		_bridge.PostToWeb(
			$"{{\"type\":\"scratch-saved\",\"scratchPath\":{JsonString(scratchPath)},\"savedPath\":{JsonString(savedPath)},\"reopen\":{(reopen ? "true" : "false")}}}");

	/// <summary>Encodes a string as a JSON string literal (trim-safe; no reflection).</summary>
	private static string JsonString(string value) => "\"" + JsonEncodedText.Encode(value) + "\"";

	/// <summary>The correlation <c>id</c> of an fs-stat/read/write request.</summary>
	private static string FsId(JsonElement root) =>
		root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;

	/// <summary>The native <c>path</c> of an fs-stat/read/write request.</summary>
	private static string FsPath(JsonElement root) =>
		root.TryGetProperty("path", out var pathEl) ? pathEl.GetString() ?? string.Empty : string.Empty;

	/// <summary>
	/// Routes a terminal message to the controller for its <c>slot</c> (the workspace session it names) and
	/// <c>session</c> pane (default: claude). Each loaded session has its own live panes, so the message carries
	/// which session it came from — input/resize/ready from a background session's pane must reach THAT session's
	/// controller, not the active one. Falls back to the active session when the slot is absent or no longer
	/// loaded.
	/// </summary>
	private TerminalController? TerminalFor(JsonElement root) {
		string? pane = root.TryGetProperty("session", out var s) ? s.GetString() : null;
		string? slot = root.TryGetProperty("slot", out var sl) ? sl.GetString() : null;
		var session = !string.IsNullOrEmpty(slot) ? _sessions?.Find(slot)?.Session : null;
		session ??= _session;
		return pane == "shell" ? session?.Shell : session?.Claude;
	}
}
