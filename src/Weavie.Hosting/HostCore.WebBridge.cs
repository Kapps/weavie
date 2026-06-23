using System.Text.Json;
using Weavie.Core.Changes;
using Weavie.Core.Commands;
using Weavie.Core.Editor;
using Weavie.Core.Git;
using Weavie.Core.Json;
using Weavie.Core.Layout;
using Weavie.Core.Lsp;
using Weavie.Core.Remote;
using Weavie.Core.Sessions;
using Weavie.Core.Shell;
using Weavie.Core.Theming;
using Weavie.Core.Workspaces;

namespace Weavie.Hosting;

// The web <-> host message bridge: the inbound OnWebMessage dispatcher and every outbound Push*ToWeb helper,
// all routed through the active session (_session) and the bridge, so it's identical on every host.
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
				string switchId = root.GetStringOrEmpty("id");
				if (!string.IsNullOrEmpty(switchId) && _sessions?.Find(switchId) is { } target) {
					SwitchToSlot(target);
				}

				break;
			}

			case "new-session": {
				string? branch = root.GetStringOrNull("branch");
				// "existing" ⇒ check out the named branch into a new worktree rather than creating a new branch.
				bool existing = root.GetBoolOrFalse("existing");
				// Normalize the page's base ("head"/"main") to "current"/"main"; ignored for an existing-branch checkout.
				string? baseSpec = root.GetStringOrNull("base");
				string? resolvedBase = baseSpec is null
					? null
					: string.Equals(baseSpec, "main", StringComparison.OrdinalIgnoreCase) ? "main" : "current";
				_ = CreateSessionFromWebAsync(branch, resolvedBase, existing);
				break;
			}

			case "list-branches": {
				_ = ListBranchesForWebAsync(root.GetStringOrEmpty("id"));
				break;
			}

			case "delete-session-request": {
				_ = DeleteSessionPromptAsync(root.GetStringOrEmpty("id"));
				break;
			}

			case "delete-session": {
				_ = DeleteSessionFromWebAsync(root.GetStringOrEmpty("id"), root.GetBoolOrFalse("force"));
				break;
			}

			case "diff-resolved":
				string diffId = root.GetProperty("id").GetString() ?? string.Empty;
				// Route by owning session (diff ids are process-unique): a switch mid-resolve must not hit another session's diff.
				if (!ResolveDiff(diffId, root.GetBoolOrFalse("kept"), root.GetStringOrNull("finalContents"))) {
					Log($"[weavie] diff-resolved for unknown id '{diffId}' — no loaded session owns it (switch-race or double-resolve?)");
				}

				break;
			case "reveal-file":
				string revealPath = root.GetProperty("path").GetString() ?? string.Empty;
				_session?.FileOpener.Open(
					revealPath, root.GetIntOr("line", 1), preview: root.GetBoolOrFalse("preview"), scratch: false);
				break;
			case "list-dir":
				_session?.ListDirectory(root.GetStringOrEmpty("path"));
				break;
			case "active-editor-changed":
				// Attribute by the page-stamped owner: a selection emit that lands after a switch must update the session it was FOR, or be rejected.
				EditorMessageTarget(root, "active-editor-changed")?.UpdateActiveEditor(root);
				break;
			case "open-editors-changed":
				EditorMessageTarget(root, "open-editors-changed")?.UpdateOpenEditors(root);
				break;
			case "get-turn-diff":
				PushTurnDiffToWeb(root.GetProperty("path").GetString() ?? string.Empty);
				break;
			case "fs-stat":
				// Route by path, not active session: the statted file belongs to whichever session's worktree contains it.
				if (ResolveFsSession(FsPath(root)) is { } statSession) {
					_bridge.PostToWeb(statSession.FileProvider.Stat(FsId(root), FsPath(root)));
				} else {
					_bridge.PostToWeb(FileProviderProtocol.StatResult(FsId(root), default));
				}

				break;
			case "fs-read":
				if (ResolveFsSession(FsPath(root)) is { } readSession) {
					_bridge.PostToWeb(readSession.FileProvider.Read(FsId(root), FsPath(root)));
				} else {
					_bridge.PostToWeb(FileProviderProtocol.ReadNotFound(FsId(root)));
				}

				break;
			case "fs-write":
				// Data safety on a switch: the web flushes the outgoing session's working copies as fs-writes during
				// rebind, which can land after _session flipped — routing by path saves them on the owning session.
				if (ResolveFsSession(FsPath(root)) is { } writeSession) {
					_bridge.PostToWeb(writeSession.FileProvider.Write(
						FsId(root), FsPath(root), root.GetStringOrEmpty("content")));
				} else {
					_bridge.PostToWeb(FileProviderProtocol.WriteError(FsId(root), "Path is outside every session worktree."));
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
			case "revert-file":
				RevertFile(root);
				break;
			case "invoke-command":
				// A web keybinding/palette/menu invoked a Core command — run it on the active session. A `token`
				// asks for a command-result reply (request/response); without one it stays fire-and-forget.
				InvokeCommandFromWeb(
					root.GetStringOrEmpty("id"),
					root.TryGetProperty("args", out var caEl) && caEl.ValueKind == JsonValueKind.Object ? caEl.GetRawText() : null,
					root.GetStringOrNull("token"));
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
				// Build the omnibar's quick-open index from the active session's worktree, so switching re-roots "Go to File".
				PushFileIndexToWeb();
				break;
			case "ready":
				// The page's bridge listener is live; push the persisted state to restore it. Must go on `ready`, not
				// init — PostToWeb before navigation no-ops (window.__weavieReceive doesn't exist yet).
				PushLayoutToWeb();
				PushEditorSessionToWeb();
				PushSessionList();
				PushRemoteAgentsToWeb();
				PushRailStateToWeb();
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
				_session?.Scratch.Delete(root.GetStringOrEmpty("path"));
				break;
			case "add-remote-agent": {
				// The web validated the runner (it owns the connection); persist the agent here so it survives
				// restart. The store's Changed re-pushes the registry to every window's page.
				var agent = new RemoteAgent(
					root.GetStringOrEmpty("name"), root.GetStringOrEmpty("url"), root.GetStringOrEmpty("token"));
				if (!string.IsNullOrEmpty(agent.Name)) {
					_remoteAgents.Add(agent);
				}

				break;
			}

			case "remove-remote-agent":
				_remoteAgents.Remove(root.GetStringOrEmpty("name"));
				break;
			case "set-last-location":
				// The page remembers where the last session was created (a backend id); persist it for next launch.
				_railState.SetLastLocation(root.GetStringOrEmpty("location"));
				break;
			case "set-promoted":
				// The page's promoted-remote-session set changed; persist the full set it sent.
				_railState.SetPromoted(StringArray(root, "promoted"));
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

	/// <summary>
	/// Pushes the persisted remote-agent registry (with each runner's URL + token) so the page connects to each
	/// agent and offers it as a New Session location. The host owns persistence; the web owns the connections.
	/// </summary>
	private void PushRemoteAgentsToWeb() =>
		_bridge.PostToWeb(JsonSerializer.Serialize(new {
			type = "remote-agents",
			agents = _remoteAgents.Agents.Select(a => new { name = a.Name, url = a.Url, token = a.Token }),
		}));

	/// <summary>
	/// Pushes the session rail's persisted UI state (last-used backend + promoted remote sessions) so the page
	/// restores its working set and the New Session prompt's default location. Honored only from the local backend.
	/// </summary>
	private void PushRailStateToWeb() =>
		_bridge.PostToWeb(JsonSerializer.Serialize(new {
			type = "rail-state",
			lastLocation = _railState.LastLocation,
			promoted = _railState.Promoted,
		}));

	/// <summary>Pushes the persisted/reconciled layout document to the web app as a compact set-layout message.</summary>
	private void PushLayoutToWeb() {
		string documentJson = LayoutSerialization.SerializeCompact(_layout.Current);
		_bridge.PostToWeb($"{{\"type\":\"set-layout\",\"document\":{documentJson}}}");
	}

	/// <summary>Applies an editor session the web sent (open files + view state); records it on the OWNING session (validated against the active one), persisting the primary's.</summary>
	private void HandleEditorSessionChanged(JsonElement root) {
		// Attribute by the stamped owner: a debounced send landing after a switch belongs to the previous session
		// and must be rejected, not written into the now-active one (cross-worktree tab contamination).
		if (EditorMessageTarget(root, "editor-session-changed") is not { } target) {
			return;
		}

		if (!root.TryGetProperty("session", out var sessionElement)) {
			return;
		}

		if (!EditorSessionSerialization.TryDeserialize(sessionElement.GetRawText(), out var session, out string? error)
			|| session is null) {
			Log($"[weavie] editor-session-changed: bad session ({error})");
			return;
		}

		// Record on the owning session so a switch rebinds the editor to its worktree's tabs. The primary also
		// mirrors to the persisted per-workspace store (launch restore); worktree sessions are in-memory only.
		target.EditorSession = session;
		if (ReferenceEquals(target, _primarySession)) {
			_editorSession.Update(session);
		}
	}

	/// <summary>
	/// The session a page→host editor message is FOR, by the id the page stamps from the last
	/// <c>set-editor-session</c>: a send produced before a switch but processed after it carries the previous id
	/// and is rejected (loudly). An unstamped message falls back to the active session; returns <c>null</c> when
	/// there is no active session or the id doesn't match it.
	/// </summary>
	private HostSession? EditorMessageTarget(JsonElement root, string kind) {
		if (_session is not { } active) {
			return null;
		}

		string? owner = root.TryGetProperty("sessionId", out var ownerEl) && ownerEl.ValueKind == JsonValueKind.String
			? ownerEl.GetString()
			: null;
		if (string.IsNullOrEmpty(owner)) {
			return active; // unstamped — attribute to the active session (transitional / older page)
		}

		if (!string.Equals(owner, active.Id, StringComparison.Ordinal)) {
			Log($"[weavie] {kind} for session '{owner}' ignored — active session is '{active.Id}' (stale post-switch message)");
			return null;
		}

		return active;
	}

	/// <summary>Pushes the persisted editor session for launch restore, scoped to the primary session's root and
	/// stamped with its id so a later change can't be misattributed.</summary>
	private void PushEditorSessionToWeb() {
		if (_primarySession is not { } primary) {
			return;
		}

		_bridge.PostToWeb(EditorSessionStore.BuildRestoreJson(
			_editorSession.Current, primary.FileSystem, primary.WorkspaceRoot, primary.Id, Log));
	}

	/// <summary>
	/// Re-points the page's language clients at a session's own LSP bridge on a switch (each session has its own,
	/// rooted at its worktree). Without it a switched-in worktree session's hover/diagnostics/go-to-def would keep
	/// resolving against the primary's checkout (whose config is the one injected at launch).
	/// </summary>
	private void PushLspConfigToWeb(HostSession session) =>
		_bridge.PostToWeb($"{{\"type\":\"lsp-config\",\"config\":{session.LspConfigJson}}}");

	/// <summary>
	/// Re-walks the active session's worktree and pushes its <c>file-index</c> so the omnibar's "Go to File" and
	/// the file browser re-root to it. Runs off the UI thread; drops the result if the user switched again first,
	/// so a slow walk from a stale session can't clobber the page's index.
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
	/// Pushes the per-turn change list (each changed file + its first-change line) for the page's review
	/// navigator, with the host-decided auto-open flag (see <see cref="ShouldOpenReview"/>). Driven by the change
	/// tracker, which records edits in every permission mode, so it's the review surface in default mode too.
	/// </summary>
	private void PushTurnChangesToWeb() {
		if (_session is { } session) {
			_bridge.PostToWeb(ChangeMessages.TurnChanges(session.Changes, ShouldOpenReview(session)));
		}
	}

	/// <summary>
	/// Decides — and records — whether the page should auto-open the first review file: true when the session is
	/// settled with auto-applied changes it hasn't opened yet. Armed once per session + first changed file, so
	/// more edits in the same idle don't re-jump the editor while a new turn or a switch re-arms.
	/// </summary>
	private bool ShouldOpenReview(HostSession session) {
		// Settled = Idle (turn ended) or NeedsInput (waiting on input); both mean edits are ready to review. Only
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
	/// Clears the page's inline turn-review markers (and the stale per-file diffs) — the outgoing half of a switch.
	/// Must run BEFORE the incoming session's editor channel re-renders its held openDiff, or the reset's
	/// <c>clearAll</c> would wipe that diff (it lives in the same inline-diff registry).
	/// </summary>
	private void ResetReviewMarkers() => _bridge.PostToWeb(ChangeMessages.TurnReset());

	/// <summary>
	/// Pushes the incoming session's inline turn-review set onto the page after a switch — the inbound half (markers
	/// already cleared by <see cref="ResetReviewMarkers"/>). The live turn pushes are gated on <c>IsActiveSession</c>,
	/// so a session that edited while muted has a tracker the page never heard about (an empty set also clears the
	/// stale ← / → walk). The arm key resets first so it re-opens its review on switch-in.
	/// </summary>
	private void PushIncomingReviewState() {
		if (_session is not { } session) {
			return;
		}

		_armedReviewKey = null;
		_bridge.PostToWeb(ChangeMessages.TurnChanges(session.Changes, ShouldOpenReview(session)));
	}

	/// <summary>Pushes a live-refresh of one edited file via an <c>fs-change</c> (VSCode reloads the non-dirty model from disk).</summary>
	private void PushRefreshToWeb(string path) {
		if (_session?.Changes.Get(path) is { } change) {
			_bridge.PostToWeb(FileProviderProtocol.Changed(change.Path, "updated"));
		}
	}

	/// <summary>
	/// Pushes an <c>fs-change</c> removal for a file deleted mid-turn so the page closes its tab and clears the
	/// inline marker (the <see cref="PushRefreshToWeb"/> counterpart). Reaches files the workspace watcher doesn't
	/// (it filters by extension), so a created-then-deleted scratch file can't strand the ← / → walk on a dead path.
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
	/// Pushes one file's per-turn diff so the page renders it inline. Driven by the change tracker (which records
	/// edits in every mode), so the inline markers are the review surface in default mode too.
	/// </summary>
	private void PushTurnDiffToWeb(string path) {
		if (_session is { } session && session.Changes.GetTurn(path) is { } turn) {
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
	/// the editor. The delete-vs-truncate rule lives in <see cref="SessionChangeTracker.RevertFile"/> (shared by
	/// per-hunk/per-file/whole-set reverts); the host only keeps the workspace guard and the editor pushes.
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
	/// Reverts a single hunk on disk: the web sends the hunk's line ranges and a <c>guardText</c> snapshot, and
	/// Core splices its own baseline lines back in (never the message's). A guard mismatch (a parallel edit moved
	/// the file) aborts without writing and re-emits a fresh diff; reverting a created file's last hunk deletes it.
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
	/// Reverts one file to its review baseline on disk — the file-scoped analogue of <see cref="UndoTurn"/>,
	/// sharing <see cref="SessionChangeTracker.RevertFile"/>. Workspace-guards the path, refreshes the editor, and
	/// re-emits the review set so the now-clean file leaves the ← / → walk.
	/// </summary>
	private void RevertFile(JsonElement root) {
		if (_session is not { } session) {
			return;
		}

		string path = root.GetStringOrEmpty("path");
		if (string.IsNullOrEmpty(path)) {
			return;
		}

		if (!BufferStore.IsWithinWorkspace(session.WorkspaceRoot, path)) {
			Notify("error", $"Couldn't revert {Path.GetFileName(path)}: path is outside the workspace.");
			return;
		}

		try {
			PushAfterRevert(path, session.Changes.RevertFile(path));
			PushTurnChangesToWeb(); // the file has left the review set
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Notify("error", $"Couldn't revert {Path.GetFileName(path)}: {ex.Message}");
		}
	}

	/// <summary>
	/// Pushes the editor refresh for a completed revert: an <c>fs-change</c> removal for a deleted file, else a
	/// reload plus a fresh per-file diff so the reverted markers drop.
	/// </summary>
	private void PushAfterRevert(string path, RevertHunkOutcome outcome) {
		if (outcome == RevertHunkOutcome.Deleted) {
			_bridge.PostToWeb(FileProviderProtocol.Changed(path, "deleted"));
		} else {
			PushRefreshToWeb(path);  // fs-change → the editor reloads the rewritten file
			PushTurnDiffToWeb(path); // the inline diff drops the reverted hunk(s)
		}
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
	/// Runs a Core command on the active session from a native trigger (e.g. the macOS menu bar), the same path
	/// the web's <c>invoke-command</c> takes. Fire-and-forget.
	/// </summary>
	public void InvokeCommand(string id) => InvokeCommandFromWeb(id, null, null);

	/// <summary>Runs a Core command with JSON arguments on the active session (native-trigger overload).</summary>
	public void InvokeCommand(string id, string? argsJson) => InvokeCommandFromWeb(id, argsJson, null);

	/// <summary>Pushes a user-facing notification (rendered as a toast in the page).</summary>
	public void Notify(string level, string message) =>
		_bridge.PostToWeb($"{{\"type\":\"notify\",\"level\":{JsonString(level)},\"message\":{JsonString(message)}}}");

	/// <summary>
	/// Creates a session from the page's <c>new-session</c> request and surfaces any failure as a toast (the
	/// rail's "+" is fire-and-forget, so the error would otherwise only reach the log).
	/// </summary>
	private async Task CreateSessionFromWebAsync(string? branch, string? baseSpec, bool attachExisting) {
		var result = await NewSessionAsync(
			new NewSessionRequest { Branch = branch, Base = baseSpec, AttachExisting = attachExisting }, CancellationToken.None).ConfigureAwait(false);
		if (!result.Ok) {
			Notify("error", result.Error ?? "Couldn't create the session.");
		}
	}

	/// <summary>
	/// Answers the new-session dialog's branch typeahead: local branches checkout-able as a new session — all
	/// minus those already in a worktree (git won't let a second worktree attach). Replies with a
	/// <c>branches-result</c> tagged by <paramref name="id"/>; a non-repo or git failure yields an empty list.
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
	/// Handles the rail's <c>delete-session-request</c>: classifies the worktree state (clean/untracked/modified)
	/// and asks the page for the matching confirm, so the user never sees "branch is kept" when work would be lost.
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

		// A gone/half-removed worktree (no .git) can't be inspected and has nothing left to lose — prompt as clean
		// so the user can still complete the delete (which just reconciles the leftover bookkeeping).
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
	/// Handles the page's confirmed <c>delete-session</c> and toasts the outcome. <paramref name="force"/> is set
	/// for a dirty worktree so the removal isn't refused.
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

	/// <summary>
	/// Runs a Core command the web asked for on the active session. A non-null <paramref name="token"/> requests a
	/// <c>command-result</c> reply (request/response); without one it's fire-and-forget and failures are logged.
	/// </summary>
	private void InvokeCommandFromWeb(string id, string? argsJson, string? token) {
		if (_session is null || string.IsNullOrEmpty(id)) {
			if (!string.IsNullOrEmpty(token)) {
				PostCommandResult(token, CommandResult.Failure("No active session."));
			}

			return;
		}

		_ = RunCommandSafeAsync(id, argsJson, token);
	}

	private async Task RunCommandSafeAsync(string id, string? argsJson, string? token) {
		if (_session is null) {
			if (!string.IsNullOrEmpty(token)) {
				PostCommandResult(token, CommandResult.Failure("No active session."));
			}

			return;
		}

		CommandResult result;
		try {
			result = await _session.Commands.InvokeAsync(id, argsJson, CancellationToken.None).ConfigureAwait(false);
		} catch (Exception ex) when (ex is UnknownCommandException or InvalidOperationException) {
			result = CommandResult.Failure(ex.Message);
		}

		if (!result.Ok) {
			Log($"[weavie] invoke-command {id} failed: {result.Error}");
		}

		if (!string.IsNullOrEmpty(token)) {
			PostCommandResult(token, result);
		}
	}

	/// <summary>Replies to a tokened <c>invoke-command</c> with its outcome; <c>data</c> embeds the result's raw-JSON payload verbatim (null when absent).</summary>
	private void PostCommandResult(string token, CommandResult result) =>
		_bridge.PostToWeb(
			$"{{\"type\":\"command-result\",\"token\":{JsonString(token)},\"ok\":{(result.Ok ? "true" : "false")},"
			+ $"\"message\":{JsonOrNull(result.Message)},\"error\":{JsonOrNull(result.Error)},\"data\":{result.DataJson ?? "null"}}}");

	/// <summary>A JSON string literal for <paramref name="value"/>, or the literal <c>null</c> when it's null.</summary>
	private static string JsonOrNull(string? value) => value is null ? "null" : JsonString(value);

	/// <summary>
	/// The dispatcher's web invoker — how Claude's <c>runCommand</c> of a web command reaches the UI: posts a
	/// <c>run-command</c> to the page and awaits its <c>command-ack</c> (or a 5s timeout).
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
	/// Saves a scratch (untitled) buffer under a real name via the native Save-As dialog, deletes the temp, and
	/// replies <c>scratch-saved</c>. <c>reopen</c> is true only for an in-workspace target (the editor can't edit
	/// out-of-workspace files); replies cancelled when the host has no native dialog.
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
	/// Routes a terminal message to the controller for its <c>slot</c> (the session it names) and <c>session</c>
	/// pane (default: claude): input/resize/ready from a background session's pane must reach THAT session's
	/// controller, not the active one. Falls back to the active session when the slot is absent or no longer loaded.
	/// </summary>
	private TerminalController? TerminalFor(JsonElement root) {
		string? pane = root.TryGetProperty("session", out var s) ? s.GetString() : null;
		string? slot = root.TryGetProperty("slot", out var sl) ? sl.GetString() : null;
		var session = !string.IsNullOrEmpty(slot) ? _sessions?.Find(slot)?.Session : null;
		// Flag a named-but-unresolvable slot: misrouted input/resize is a real bug the fallback shouldn't hide. An
		// absent slot is the older protocol — silent.
		if (!string.IsNullOrEmpty(slot) && session is null) {
			Log($"[weavie] term message named slot '{slot}' which isn't loaded — falling back to the active session '{_session?.Id}'");
		}

		session ??= _session;
		return pane == "shell" ? session?.Shell : session?.Claude;
	}
}
