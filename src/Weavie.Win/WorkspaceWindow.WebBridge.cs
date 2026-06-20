using System.Text.Json;
using Weavie.Core.Changes;
using Weavie.Core.Commands;
using Weavie.Core.Editor;
using Weavie.Core.Layout;
using Weavie.Core.Lsp;
using Weavie.Core.Mcp;
using Weavie.Core.Sessions;
using Weavie.Core.Shell;
using Weavie.Core.Workspaces;
using Weavie.Win.Hosting;

namespace Weavie.Win;

// The web<->host message bridge for the workspace window: the inbound OnWebMessage dispatcher and every
// outbound Push*ToWeb / command / turn helper. Split from WorkspaceWindow.cs (which keeps the Form, window
// chrome, and session-init wiring) so each file holds one concern.
internal sealed partial class WorkspaceWindow {
	private void OnWebMessage(string json) {
		string type;
		JsonElement root;
		try {
			using var doc = JsonDocument.Parse(json);
			root = doc.RootElement.Clone();
			type = root.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty;
		} catch (JsonException) {
			Console.WriteLine($"[weavie] (unparsed) {json}");
			return;
		}

		switch (type) {
			case "term-input":
				byte[] input = Convert.FromBase64String(root.GetProperty("dataB64").GetString() ?? string.Empty);
				if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEAVIE_DEBUG_INPUT"))) {
					string printable = string.Concat(input.Select(b => b is >= 0x20 and < 0x7f ? ((char)b).ToString() : $"\\x{b:x2}"));
					Console.WriteLine($"[weavie] term-input <- xterm ({input.Length}B): {printable}");
					Console.Out.Flush();
				}
				TerminalFor(root)?.Write(input);
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
				// The page sends base "head" (the active session's HEAD) or "main"; ResolveBaseRefAsync treats
				// anything other than "main" as the current session's HEAD, so normalize to "current"/"main".
				string? baseSpec = root.TryGetProperty("base", out var nbEl) ? nbEl.GetString() : null;
				string? resolvedBase = baseSpec is null
					? null
					: string.Equals(baseSpec, "main", StringComparison.OrdinalIgnoreCase) ? "main" : "current";
				_ = CreateSessionFromWebAsync(branch, resolvedBase);
				break;
			}

			case "close-session": {
				string closeId = root.TryGetProperty("id", out var csEl) ? csEl.GetString() ?? string.Empty : string.Empty;
				_ = CloseSessionAsync(closeId, CancellationToken.None);
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
			case "get-change-diff":
				PushChangeDiffToWeb(root.GetProperty("path").GetString() ?? string.Empty);
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
			case "invoke-command":
				// A keybinding/palette in the web invoked a Core command — run it here (fire-and-forget; the
				// web doesn't await a result for its own triggers).
				InvokeCommandFromWeb(
					root.TryGetProperty("id", out var ciEl) ? ciEl.GetString() ?? string.Empty : string.Empty,
					root.TryGetProperty("args", out var caEl) && caEl.ValueKind == JsonValueKind.Object ? caEl.GetRawText() : null);
				break;
			case "command-ack":
				// The web finished a run-command (a web command Claude invoked over MCP) — settle the pending await.
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
				// Build the omnibar's quick-open index from the ACTIVE session's worktree (not the primary's),
				// so switching sessions re-roots "Go to File" to the files that session can actually open.
				PushFileIndexToWeb();
				break;
			case "ready":
				// The page's bridge listener is live; push the persisted layout so it restores on launch, the
				// persisted editor session so the editor reopens its files, the session list so the rail shows
				// the primary session (+ the "+"), and the initial window state so the title bar's maximize
				// glyph + blur dim start correct. These must go on `ready`, NOT during init — PostToWeb before
				// navigation no-ops (window.__weavieReceive doesn't exist yet).
				PushLayoutToWeb();
				PushEditorSessionToWeb();
				PushSessionList();
				PushWindowState();
				Console.WriteLine($"[weavie] {json}");
				Console.Out.Flush();
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
				SaveScratchAs(root);
				break;
			case "discard-scratch":
				// The user closed (and confirmed discarding) a scratch buffer: delete its temp file.
				_session?.Scratch.Delete(root.TryGetProperty("path", out var dsEl) ? dsEl.GetString() ?? string.Empty : string.Empty);
				break;
			default:
				// log — surface for diagnostics and unattended capture.
				Console.WriteLine($"[weavie] {json}");
				Console.Out.Flush();
				break;
		}
	}

	/// <summary>The correlation <c>id</c> of an fs-stat/read/write request.</summary>
	private static string FsId(JsonElement root) =>
		root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;

	/// <summary>The native <c>path</c> of an fs-stat/read/write request.</summary>
	private static string FsPath(JsonElement root) =>
		root.TryGetProperty("path", out var pathEl) ? pathEl.GetString() ?? string.Empty : string.Empty;

	/// <summary>Routes a terminal message to the controller for its <c>session</c> pane (default: claude).</summary>
	private TerminalController? TerminalFor(JsonElement root) {
		string? pane = root.TryGetProperty("session", out var s) ? s.GetString() : null;
		return pane == "shell" ? _session?.Shell : _session?.Claude;
	}

	/// <summary>Applies a layout the web sent (split/focus change) through the store, which validates + persists it.</summary>
	private void HandleLayoutChanged(JsonElement root) {
		if (!root.TryGetProperty("document", out var documentElement)) {
			return;
		}

		if (!LayoutSerialization.TryDeserialize(documentElement.GetRawText(), out var document, out string? error)
			|| document is null) {
			Console.WriteLine($"[weavie] layout-changed: bad document ({error})");
			return;
		}

		try {
			_layout.SetPanes(document.Root, document.Focused, LayoutSource.User);
		} catch (LayoutValidationException ex) {
			Console.WriteLine($"[weavie] layout-changed rejected: {ex.Message}");
		}
	}

	/// <summary>Pushes the persisted/reconciled layout document to the web app as a compact set-layout message.</summary>
	private void PushLayoutToWeb() {
		string documentJson = LayoutSerialization.SerializeCompact(_layout.Current);
		_bridge.PostToWeb($"{{\"type\":\"set-layout\",\"document\":{documentJson}}}");
	}

	/// <summary>Applies an editor session the web sent (open files + view state) through the store, which persists it.</summary>
	private void HandleEditorSessionChanged(JsonElement root) {
		if (!root.TryGetProperty("session", out var sessionElement)) {
			return;
		}

		if (!EditorSessionSerialization.TryDeserialize(sessionElement.GetRawText(), out var session, out string? error)
			|| session is null) {
			Console.WriteLine($"[weavie] editor-session-changed: bad session ({error})");
			return;
		}

		// Record on the active session so a switch can rebind the editor to its worktree's tabs. The primary
		// also mirrors to the persisted per-workspace store (for launch restore); secondary worktree sessions
		// are in-memory only for now.
		if (_session is { } active) {
			active.EditorSession = session;
			if (ReferenceEquals(active, _primarySession)) {
				_editorSession.Update(session);
			}
		}
	}

	/// <summary>Pushes the persisted editor session (open file paths + view state) for launch restore.</summary>
	private void PushEditorSessionToWeb() => _bridge.PostToWeb(_editorSession.BuildRestoreJson());

	/// <summary>
	/// Re-walks the ACTIVE session's worktree and pushes its <c>file-index</c> (root + every file's absolute
	/// path) so the omnibar's "Go to File" and the file browser re-root to the active session. Built from the
	/// active session's <see cref="HostSession.FileIndex"/>, so after a switch quick-open lists the worktree's
	/// own files — never the primary's, which the worktree's file provider would refuse to read (an out-of-root
	/// path surfaces as "nonexistent file"). Runs the walk off the UI thread (up to
	/// <see cref="WorkspaceFileIndex.DefaultCap"/> files), and drops the result if the user switched again
	/// before it finished, so a slow walk from a stale session can't clobber the page's index. Called on the
	/// omnibar's request-file-index and on every session switch.
	/// </summary>
	private void PushFileIndexToWeb() {
		if (_session is not { } session) {
			return;
		}

		_ = Task.Run(() => {
			var files = session.FileIndex.List(WorkspaceFileIndex.DefaultCap);
			// Only the still-active session may drive the page's index (a reference read is atomic).
			if (ReferenceEquals(_session, session)) {
				_bridge.PostToWeb(ShellProtocol.BuildFileIndex(session.FileIndex.Root, files));
			}
		});
	}

	/// <summary>Pushes the session change list (each file's path + added/removed line counts) to the page.</summary>
	private void PushChangesToWeb() {
		if (_session is not null) {
			_bridge.PostToWeb(ChangeMessages.SessionChanges(_session.Changes));
		}
	}

	/// <summary>
	/// Pushes the per-turn change list (each file changed this turn + its first-change line) for the page's
	/// review navigator. Only in an auto-keep mode (acceptEdits/bypass): that's where post-turn review is the
	/// surface — default mode reviews each edit via the blocking openDiff, so there's nothing to list.
	/// </summary>
	private void PushTurnChangesToWeb() {
		if (!PermissionModeDiffPresenter.AutoKeepsEdits(_settings)) {
			return;
		}

		if (_session is not null) {
			_bridge.PostToWeb(ChangeMessages.TurnChanges(_session.Changes));
		}
	}

	/// <summary>Pushes one file's session diff (baseline vs. current text) to the page for the changes view.</summary>
	private void PushChangeDiffToWeb(string path) {
		if (_session?.Changes.Get(path) is { } change) {
			_bridge.PostToWeb(ChangeMessages.ChangeDiff(change));
		}
	}

	/// <summary>
	/// Pushes a live-refresh of one edited file. The editor's file models are VSCode working copies behind the
	/// host-backed <c>file://</c> provider, so the reload is driven by an <c>fs-change</c> push (the provider
	/// fires its change event → VSCode reloads the non-dirty model from disk). The legacy <c>refresh-file</c>
	/// message is no longer sent; <c>fs-change</c> is the single reload path (shared with the watcher).
	/// </summary>
	private void PushRefreshToWeb(string path) {
		if (_session?.Changes.Get(path) is { } change) {
			_bridge.PostToWeb(FileProviderProtocol.Changed(change.Path, "updated"));
		}
	}

	/// <summary>
	/// Forwards a workspace-watcher batch (non-Claude on-disk edits) to the page's <c>file://</c> provider as an
	/// <c>fs-change</c>. Fires off the UI thread (the watcher's timer); <c>PostToWeb</c> marshals.
	/// </summary>
	private void PushWatcherChangesToWeb(IReadOnlyList<WatchedFileChange> changes) {
		if (FileProviderProtocol.WatchedChanges(changes) is { } json) {
			_bridge.PostToWeb(json);
		}
	}

	/// <summary>
	/// Pushes one file's per-turn diff so the page renders it inline in the live editor. Only in an auto-keep
	/// mode (acceptEdits/bypass): there the applied turn-markers are the review surface. In default mode every
	/// edit is reviewed via the blocking openDiff Keep/Reject, so a second applied marker would just demand a
	/// redundant Accept — suppress it.
	/// </summary>
	private void PushTurnDiffToWeb(string path) {
		if (!PermissionModeDiffPresenter.AutoKeepsEdits(_settings)) {
			return;
		}

		if (_session?.Changes.GetTurn(path) is { } turn) {
			_bridge.PostToWeb(ChangeMessages.TurnDiff(turn));
		}
	}

	/// <summary>Clears all inline turn markers on a turn boundary (the prior turn is implicitly accepted).</summary>
	private void PushTurnReset() => _bridge.PostToWeb(ChangeMessages.TurnReset());

	/// <summary>Accepts the whole turn's changes: resets the per-turn baseline and clears the page's inline markers.</summary>
	private void AcceptTurn() {
		if (_session is null) {
			return;
		}

		_session.Changes.AcceptTurn();
		_bridge.PostToWeb(ChangeMessages.TurnReset());
	}

	/// <summary>
	/// Undoes the whole turn's changes: reverts every file touched this turn to its turn baseline on disk and
	/// live-refreshes the editor. Files created this turn truncate to empty (not deleted) — surfaced via a toast.
	/// </summary>
	private void UndoTurn() {
		if (_session is null) {
			return;
		}

		var truncated = new List<string>();
		foreach (var change in _session.Changes.TurnChanges()) {
			try {
				if (!BufferStore.Save(_session.FileSystem, _session.WorkspaceRoot, change.Path, change.BaselineText)) {
					Notify("error", $"Couldn't undo {Path.GetFileName(change.Path)}: path is outside the workspace.");
					continue;
				}

				if (change.BaselineText.Length == 0) {
					truncated.Add(Path.GetFileName(change.Path));
				}

				// Re-read disk (now the baseline) into the tracker → fires FileChanged → refresh + empty turn diff.
				_session.Changes.RecordChange(change.Path);
			} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
				Notify("error", $"Couldn't undo {Path.GetFileName(change.Path)}: {ex.Message}");
			}
		}

		if (truncated.Count > 0) {
			Notify("warn", $"Undo emptied {truncated.Count} file(s) created this turn (delete manually): {string.Join(", ", truncated)}");
		}
	}

	/// <summary>Pushes a user-facing notification (rendered as a toast in the page).</summary>
	private void Notify(string level, string message) =>
		_bridge.PostToWeb(JsonSerializer.Serialize(new { type = "notify", level, message }));

	/// <summary>
	/// Creates a session from the page's <c>new-session</c> request and surfaces any failure as a toast. The
	/// rail's "+" is fire-and-forget, so without this a "branch already exists" error would only reach the log.
	/// </summary>
	private async Task CreateSessionFromWebAsync(string? branch, string? baseSpec) {
		var result = await NewSessionAsync(
			new NewSessionRequest { Branch = branch, Base = baseSpec }, CancellationToken.None).ConfigureAwait(true);
		if (!result.Ok) {
			Notify("error", result.Error ?? "Couldn't create the session.");
		}
	}

	/// <summary>
	/// Runs a Core command the web asked for (its keybinding/palette resolved to a <see cref="CommandLocation.Core"/>
	/// command). Fire-and-forget: the web doesn't await a result for its own triggers; failures are logged.
	/// </summary>
	private void InvokeCommandFromWeb(string id, string? argsJson) {
		if (_session is null || string.IsNullOrEmpty(id)) {
			return;
		}

		_ = RunCommandSafeAsync(id, argsJson);
	}

	private async Task RunCommandSafeAsync(string id, string? argsJson) {
		try {
			var result = await _session!.Commands.InvokeAsync(id, argsJson, CancellationToken.None).ConfigureAwait(false);
			if (!result.Ok) {
				Console.WriteLine($"[weavie] invoke-command {id} failed: {result.Error}");
			}
		} catch (Exception ex) when (ex is UnknownCommandException or InvalidOperationException) {
			Console.WriteLine($"[weavie] invoke-command {id} error: {ex.Message}");
		}
	}

	/// <summary>
	/// The dispatcher's web invoker: posts a <c>run-command</c> to the page and awaits its <c>command-ack</c>
	/// (or a 5s timeout). This is how Claude's <c>runCommand</c> of a <see cref="CommandLocation.Web"/> command
	/// reaches the UI and gets an honest invoked/failed result back.
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

	/// <summary>
	/// Native <c>.vsix</c> picker for the install-from-file theme command (a WinForms <see cref="OpenFileDialog"/>
	/// on the UI thread). Returns the chosen path, or null if the user cancelled. The command dispatcher calls
	/// this off the UI thread (a runCommand/palette invocation), so the dialog is marshaled onto it.
	/// </summary>
	private Task<string?> PickVsixFileAsync(CancellationToken ct) {
		var completion = new TaskCompletionSource<string?>();
		void Show() {
			try {
				using var dialog = new OpenFileDialog {
					Title = "Install Theme from .vsix",
					Filter = "VS Code extension (*.vsix)|*.vsix|All files (*.*)|*.*",
					CheckFileExists = true,
				};
				completion.SetResult(dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null);
			} catch (Exception ex) {
				completion.SetException(ex);
			}
		}

		if (InvokeRequired) {
			BeginInvoke(Show);
		} else {
			Show();
		}

		return completion.Task;
	}

	/// <summary>Encodes a string as a JSON string literal (trim-safe; no reflection).</summary>
	private static string JsonString(string value) => "\"" + JsonEncodedText.Encode(value) + "\"";

	/// <summary>
	/// Saves a scratch (untitled) buffer under a real name: a native <see cref="SaveFileDialog"/> (defaulting to
	/// the workspace root + the buffer's "Untitled-N" name), writes its content there, deletes the temp file,
	/// and replies <c>scratch-saved</c>. <c>reopen</c> is true only when the target is inside the workspace, so
	/// the editor reopens it as a normal working copy; saved elsewhere it's written + the user is warned, but
	/// the editor can't edit out-of-workspace files, so the scratch tab just drops. OnWebMessage runs on the UI
	/// thread (WebView2 raises its message event there), so the modal dialog can be shown inline.
	/// </summary>
	private void SaveScratchAs(JsonElement root) {
		if (_session is null) {
			return;
		}

		string scratchPath = root.TryGetProperty("path", out var pEl) ? pEl.GetString() ?? string.Empty : string.Empty;
		string content = root.TryGetProperty("content", out var cEl) ? cEl.GetString() ?? string.Empty : string.Empty;
		string suggested = root.TryGetProperty("suggestedName", out var nEl) ? nEl.GetString() ?? "Untitled" : "Untitled";

		// Default the dialog to the ACTIVE session's worktree (not the primary checkout), so saving from a
		// worktree session lands in that worktree and the reopen check below recognizes it as in-workspace.
		// GetFullPath normalizes it (no trailing separator / forward slashes) so the native dialog reliably
		// honors it as the initial folder rather than falling back to the OS's last-used folder.
		string sessionRoot = Path.GetFullPath(_session.WorkspaceRoot);

		string? target;
		using (var dialog = new SaveFileDialog {
			Title = "Save As",
			InitialDirectory = sessionRoot,
			// A rooted FileName forces the dialog open at the session root even when the OS would otherwise
			// reopen its remembered last-used folder; the leaf is what shows in the name box.
			FileName = Path.Combine(sessionRoot, suggested),
			Filter = "All files (*.*)|*.*",
			OverwritePrompt = true,
			RestoreDirectory = true,
		}) {
			target = dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
		}

		if (string.IsNullOrEmpty(target)) {
			PostScratchSaved(scratchPath, string.Empty, reopen: false); // cancelled
			return;
		}

		try {
			_session.FileSystem.WriteAllText(target, content);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Notify("error", $"Couldn't save {Path.GetFileName(target)}: {ex.Message}");
			PostScratchSaved(scratchPath, string.Empty, reopen: false);
			return;
		}

		_session.Scratch.Delete(scratchPath);
		bool reopen = BufferStore.IsWithinWorkspace(_workspaceRoot, target);
		if (!reopen) {
			Notify("info", $"Saved {Path.GetFileName(target)} outside the workspace — it won't open in the editor.");
		}

		PostScratchSaved(scratchPath, target, reopen);
	}

	/// <summary>Replies to <c>save-scratch-as</c>: the saved path (empty when cancelled) + whether to reopen it.</summary>
	private void PostScratchSaved(string scratchPath, string savedPath, bool reopen) =>
		_bridge.PostToWeb(JsonSerializer.Serialize(new {
			type = "scratch-saved",
			scratchPath,
			savedPath,
			reopen,
		}));
}
