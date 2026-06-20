using System.Text;
using System.Text.Json;
using Foundation;
using UniformTypeIdentifiers;
using Weavie.Core.Changes;
using Weavie.Core.Commands;
using Weavie.Core.Editor;
using Weavie.Core.Layout;
using Weavie.Core.Lsp;
using Weavie.Core.Mcp;
using Weavie.Core.Workspaces;
using Weavie.Hosting;

namespace Weavie.Mac;

// The web <-> host message bridge for the app: the inbound OnWebMessage dispatcher and every outbound
// Push*ToWeb / command / turn / scratch helper. Split from AppDelegate.cs (which keeps app launch, the
// window, and session-init wiring) so each file holds one concern.
public sealed partial class AppDelegate {
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
				TerminalFor(root)?.Start(root.GetProperty("cols").GetInt32(), root.GetProperty("rows").GetInt32());
				break;
			case "diff-resolved":
				string diffId = root.GetProperty("id").GetString() ?? string.Empty;
				bool kept = root.TryGetProperty("kept", out var keptEl) && keptEl.GetBoolean();
				string? finalContents = root.TryGetProperty("finalContents", out var fcEl) ? fcEl.GetString() : null;
				_diffPresenter?.Resolve(diffId, kept, finalContents);
				break;
			case "reveal-file":
				string revealPath = root.GetProperty("path").GetString() ?? string.Empty;
				int revealLine = root.TryGetProperty("line", out var lnEl) ? lnEl.GetInt32() : 1;
				bool revealPreview = root.TryGetProperty("preview", out var pvEl)
					&& pvEl.ValueKind is JsonValueKind.True or JsonValueKind.False && pvEl.GetBoolean();
				_fileOpener?.Open(revealPath, revealLine, preview: revealPreview, scratch: false);
				break;
			case "active-editor-changed":
				if (_editor is not null && ActiveEditor.TryParse(root, out var activeEditor) && activeEditor is not null) {
					_editor.SetActive(activeEditor);
				}

				break;
			case "open-editors-changed":
				_editor?.SetOpenEditors(OpenEditorTab.ParseList(root));
				break;
			case "new-scratch":
				// New File (Ctrl+N): create an untitled buffer + open it as a scratch tab.
				OpenNewScratch();
				break;
			case "save-scratch-as":
				// Ctrl+S on a scratch buffer: prompt for a real name (native Save panel), write it, drop the temp.
				SaveScratchAs(root);
				break;
			case "discard-scratch":
				// The user closed (and confirmed discarding) a scratch buffer: delete its temp file.
				_scratch?.Delete(root.TryGetProperty("path", out var dsEl) ? dsEl.GetString() ?? string.Empty : string.Empty);
				break;
			case "get-change-diff":
				PushChangeDiffToWeb(root.GetProperty("path").GetString() ?? string.Empty);
				break;
			case "get-turn-diff":
				PushTurnDiffToWeb(root.GetProperty("path").GetString() ?? string.Empty);
				break;
			case "fs-stat":
				if (_fileProvider is not null) {
					_bridge.PostToWeb(_fileProvider.Stat(FsId(root), FsPath(root)));
				}

				break;
			case "fs-read":
				if (_fileProvider is not null) {
					_bridge.PostToWeb(_fileProvider.Read(FsId(root), FsPath(root)));
				}

				break;
			case "fs-write":
				if (_fileProvider is not null) {
					_bridge.PostToWeb(_fileProvider.Write(
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
				// A keybinding/palette in the web invoked a Core command — run it here (fire-and-forget).
				InvokeCommandFromWeb(
					root.TryGetProperty("id", out var ciEl) ? ciEl.GetString() ?? string.Empty : string.Empty,
					root.TryGetProperty("args", out var caEl) && caEl.ValueKind == JsonValueKind.Object ? caEl.GetRawText() : null);
				break;
			case "command-ack":
				// The web finished a run-command (a web command Claude invoked over MCP) — settle the await.
				CompleteWebCommand(root);
				break;
			case "ready":
				// The page's bridge listener is live; push the persisted layout so it restores on launch, and
				// the persisted editor session so the editor reopens its files.
				PushLayoutToWeb();
				PushEditorSessionToWeb();
				Console.WriteLine($"[weavie] {json}");
				Console.Out.Flush();
				break;
			case "layout-changed":
				HandleLayoutChanged(root);
				break;
			case "editor-session-changed":
				HandleEditorSessionChanged(root);
				break;
			case "request-file-index":
				// The omnibar's "Go to File" opened; reply with the flat workspace file list it filters over.
				PushFileIndex();
				break;
			case "list-dir":
				// The file browser opened or expanded a folder; reply with its directory listing.
				ListDirectory(root.TryGetProperty("path", out var ldEl) ? ldEl.GetString() ?? string.Empty : string.Empty);
				break;
			default:
				// log — surface for diagnostics and unattended capture.
				Console.WriteLine($"[weavie] {json}");
				Console.Out.Flush();
				break;
		}
	}

	/// <summary>Applies a layout the web sent (split/focus change) through the store, which validates + persists it.</summary>
	private void HandleLayoutChanged(JsonElement root) {
		if (_layout is null || !root.TryGetProperty("document", out var documentElement)) {
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
		if (_layout is null) {
			return;
		}

		string documentJson = LayoutSerialization.SerializeCompact(_layout.Current);
		_bridge.PostToWeb($"{{\"type\":\"set-layout\",\"document\":{documentJson}}}");
	}

	/// <summary>Applies an editor session the web sent (open files + view state) through the store, which persists it.</summary>
	private void HandleEditorSessionChanged(JsonElement root) {
		if (_editorSession is null || !root.TryGetProperty("session", out var sessionElement)) {
			return;
		}

		if (!EditorSessionSerialization.TryDeserialize(sessionElement.GetRawText(), out var session, out string? error)
			|| session is null) {
			Console.WriteLine($"[weavie] editor-session-changed: bad session ({error})");
			return;
		}

		_editorSession.Update(session);
	}

	/// <summary>Pushes the persisted editor session (with each open file's on-disk content) for launch restore.</summary>
	private void PushEditorSessionToWeb() {
		if (_editorSession is not null) {
			_bridge.PostToWeb(_editorSession.BuildRestoreJson());
		}
	}

	/// <summary>Pushes the session change list (each file's path + added/removed line counts) to the page.</summary>
	private void PushChangesToWeb() {
		if (_changes is not null) {
			_bridge.PostToWeb(ChangeMessages.SessionChanges(_changes));
		}
	}

	/// <summary>
	/// Pushes the per-turn change list (each file changed this turn + its first-change line) for the page's
	/// review navigator. Only in an auto-keep mode (acceptEdits/bypass): that's where post-turn review is the
	/// surface — default mode reviews each edit via the blocking openDiff, so there's nothing to list.
	/// </summary>
	private void PushTurnChangesToWeb() {
		if (!PermissionModeDiffPresenter.AutoKeepsEdits(_settings!)) {
			return;
		}

		if (_changes is not null) {
			_bridge.PostToWeb(ChangeMessages.TurnChanges(_changes));
		}
	}

	/// <summary>Pushes one file's session diff (baseline vs. current text) to the page for the changes view.</summary>
	private void PushChangeDiffToWeb(string path) {
		if (_changes?.Get(path) is { } change) {
			_bridge.PostToWeb(ChangeMessages.ChangeDiff(change));
		}
	}

	/// <summary>
	/// Pushes a live-refresh of one edited file. The editor's file models are VSCode working copies behind the
	/// host-backed <c>file://</c> provider, so the reload is driven by an <c>fs-change</c> push (the provider
	/// fires its change event → VSCode reloads the non-dirty model from disk). The legacy <c>refresh-file</c>
	/// message is no longer sent.
	/// </summary>
	private void PushRefreshToWeb(string path) {
		if (_changes?.Get(path) is { } change) {
			_bridge.PostToWeb(FileProviderProtocol.Changed(change.Path, "updated"));
		}
	}

	/// <summary>
	/// Pushes one file's per-turn diff so the page renders it inline in the live editor. Only in an auto-keep
	/// mode (acceptEdits/bypass), where the applied turn-markers are the review surface; in default mode openDiff
	/// is the per-edit review, so a second applied marker would just demand a redundant Accept — suppress it.
	/// </summary>
	private void PushTurnDiffToWeb(string path) {
		// _settings is set in DidFinishLaunching, before the change feed that drives this is ever wired, so it is
		// non-null by here — assert that (a violation throws loudly) rather than silently skipping the push.
		if (!PermissionModeDiffPresenter.AutoKeepsEdits(_settings!)) {
			return;
		}

		if (_changes?.GetTurn(path) is { } turn) {
			_bridge.PostToWeb(ChangeMessages.TurnDiff(turn));
		}
	}

	/// <summary>Clears all inline turn markers on a turn boundary (the prior turn is implicitly accepted).</summary>
	private void PushTurnReset() => _bridge.PostToWeb(ChangeMessages.TurnReset());

	/// <summary>
	/// Forwards a batch of on-disk changes the LSP workspace watcher reported to the page's <c>file://</c>
	/// provider, so the editor reloads the affected (non-dirty) working copies. Fires off the main thread;
	/// PostToWeb marshals.
	/// </summary>
	private void PushWatcherChangesToWeb(IReadOnlyList<WatchedFileChange> changes) {
		if (FileProviderProtocol.WatchedChanges(changes) is { } json) {
			_bridge.PostToWeb(json);
		}
	}

	/// <summary>Walks the workspace and pushes the <c>file-index</c> reply (root + every file path) for the omnibar.</summary>
	private void PushFileIndex() {
		if (_fileIndex is null) {
			return;
		}

		var files = _fileIndex.List(WorkspaceFileIndex.DefaultCap);
		var sb = new StringBuilder("{\"type\":\"file-index\",\"root\":");
		sb.Append(JsonString(_fileIndex.Root)).Append(",\"files\":[");
		for (int i = 0; i < files.Count; i++) {
			if (i > 0) {
				sb.Append(',');
			}

			sb.Append(JsonString(files[i]));
		}

		sb.Append("]}");
		_bridge.PostToWeb(sb.ToString());
	}

	/// <summary>
	/// Lists <paramref name="requestedPath"/> within the workspace and pushes a <c>dir-listing</c> reply
	/// (directories first) to the file browser. Called on open and on folder expand.
	/// </summary>
	private void ListDirectory(string requestedPath) {
		if (_browser is null) {
			return;
		}

		var entries = _browser.List(requestedPath);
		string path = string.IsNullOrEmpty(requestedPath) ? _browser.Root : requestedPath;
		var sb = new StringBuilder("{\"type\":\"dir-listing\",\"path\":");
		sb.Append(JsonString(path)).Append(",\"entries\":[");
		for (int i = 0; i < entries.Count; i++) {
			if (i > 0) {
				sb.Append(',');
			}

			var entry = entries[i];
			sb.Append("{\"name\":").Append(JsonString(entry.Name))
				.Append(",\"path\":").Append(JsonString(entry.Path))
				.Append(",\"isDir\":").Append(entry.IsDirectory ? "true" : "false").Append('}');
		}

		sb.Append("]}");
		_bridge.PostToWeb(sb.ToString());
	}

	/// <summary>Accepts the whole turn's changes: resets the per-turn baseline and clears the page's inline markers.</summary>
	private void AcceptTurn() {
		if (_changes is null) {
			return;
		}

		_changes.AcceptTurn();
		_bridge.PostToWeb(ChangeMessages.TurnReset());
	}

	/// <summary>
	/// Undoes the whole turn's changes: reverts every file touched this turn to its turn baseline on disk and
	/// live-refreshes the editor. Files created this turn truncate to empty (not deleted) — surfaced via a toast.
	/// </summary>
	private void UndoTurn() {
		if (_changes is null || _fileSystem is null || _workspace is null) {
			return;
		}

		var truncated = new List<string>();
		foreach (var change in _changes.TurnChanges()) {
			try {
				if (!BufferStore.Save(_fileSystem, _workspace, change.Path, change.BaselineText)) {
					Notify("error", $"Couldn't undo {Path.GetFileName(change.Path)}: path is outside the workspace.");
					continue;
				}

				if (change.BaselineText.Length == 0) {
					truncated.Add(Path.GetFileName(change.Path));
				}

				_changes.RecordChange(change.Path);
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
		// Built by hand (not JsonSerializer.Serialize, which is trim-unsafe — IL2026 — on the macOS target).
		_bridge.PostToWeb($"{{\"type\":\"notify\",\"level\":{JsonString(level)},\"message\":{JsonString(message)}}}");

	/// <summary>
	/// Runs a Core command the web asked for (its keybinding/palette resolved to a Core command).
	/// Fire-and-forget: the web doesn't await a result for its own triggers; failures are logged.
	/// </summary>
	private void InvokeCommandFromWeb(string id, string? argsJson) {
		if (_commands is null || string.IsNullOrEmpty(id)) {
			return;
		}

		_ = RunCommandSafeAsync(id, argsJson);
	}

	private async Task RunCommandSafeAsync(string id, string? argsJson) {
		if (_commands is null) {
			return;
		}

		try {
			var result = await _commands.InvokeAsync(id, argsJson, CancellationToken.None).ConfigureAwait(false);
			if (!result.Ok) {
				Console.WriteLine($"[weavie] invoke-command {id} failed: {result.Error}");
			}
		} catch (Exception ex) when (ex is UnknownCommandException or InvalidOperationException) {
			Console.WriteLine($"[weavie] invoke-command {id} error: {ex.Message}");
		}
	}

	/// <summary>
	/// The dispatcher's web invoker: posts a <c>run-command</c> to the page and awaits its <c>command-ack</c>
	/// (or a 5s timeout). How Claude's <c>runCommand</c> of a web command reaches the UI and gets a result back.
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

	/// <summary>
	/// Native <c>.vsix</c> picker for the install-from-file theme command (NSOpenPanel on the main thread).
	/// Returns the chosen path, or null if the user cancelled. Called off the main thread by the command
	/// dispatcher, so the modal is marshaled onto it.
	/// </summary>
	private Task<string?> PickVsixFileAsync(CancellationToken ct) {
		var completion = new TaskCompletionSource<string?>();
		InvokeOnMainThread(() => {
			var panel = NSOpenPanel.OpenPanel;
			panel.Title = "Install Theme from .vsix";
			panel.CanChooseFiles = true;
			panel.CanChooseDirectories = false;
			panel.AllowsMultipleSelection = false;
			// .vsix has no system-declared UTI, so synthesize a dynamic content type from the extension.
			if (UTType.CreateFromExtension("vsix") is { } vsixType) {
				panel.AllowedContentTypes = [vsixType];
			}
			completion.SetResult(panel.RunModal() == 1 && panel.Url is { Path: { } path } ? path : null);
		});
		return completion.Task;
	}

	/// <summary>Creates a new scratch (untitled) buffer and opens it as a scratch tab — the host side of New File (Ctrl+N).</summary>
	private void OpenNewScratch() {
		if (_scratch is null || _fileOpener is null) {
			return;
		}

		string path = _scratch.CreateNew();
		_fileOpener.Open(path, 1, preview: false, scratch: true);
	}

	/// <summary>
	/// Saves a scratch (untitled) buffer under a real name: a native <see cref="NSSavePanel"/> (defaulting to
	/// the workspace + the buffer's "Untitled-N" name), writes its content there, deletes the temp file, and
	/// replies <c>scratch-saved</c>. <c>reopen</c> is true only when the target is inside the workspace, so the
	/// editor reopens it as a normal working copy; saved elsewhere it's written + the user warned, but the editor
	/// can't edit out-of-workspace files, so the scratch tab just drops. The WKWebView raises script messages on
	/// the main thread, so the panel runs inline.
	/// </summary>
	private void SaveScratchAs(JsonElement root) {
		if (_scratch is null || _fileSystem is null || _workspace is null) {
			return;
		}

		string scratchPath = root.TryGetProperty("path", out var pEl) ? pEl.GetString() ?? string.Empty : string.Empty;
		string content = root.TryGetProperty("content", out var cEl) ? cEl.GetString() ?? string.Empty : string.Empty;
		string suggested = root.TryGetProperty("suggestedName", out var nEl) ? nEl.GetString() ?? "Untitled" : "Untitled";

		var panel = NSSavePanel.SavePanel;
		panel.Title = "Save As";
		panel.NameFieldStringValue = suggested;
		panel.DirectoryUrl = NSUrl.FromFilename(_workspace);
		string? target = panel.RunModal() == 1 && panel.Url is { Path: { } chosen } ? chosen : null;

		if (string.IsNullOrEmpty(target)) {
			PostScratchSaved(scratchPath, string.Empty, reopen: false); // cancelled
			return;
		}

		try {
			_fileSystem.WriteAllText(target, content);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Notify("error", $"Couldn't save {Path.GetFileName(target)}: {ex.Message}");
			PostScratchSaved(scratchPath, string.Empty, reopen: false);
			return;
		}

		_scratch.Delete(scratchPath);
		bool reopen = BufferStore.IsWithinWorkspace(_workspace, target);
		if (!reopen) {
			Notify("info", $"Saved {Path.GetFileName(target)} outside the workspace — it won't open in the editor.");
		}

		PostScratchSaved(scratchPath, target, reopen);
	}

	/// <summary>Replies to <c>save-scratch-as</c>: the saved path (empty when cancelled) + whether to reopen it.</summary>
	private void PostScratchSaved(string scratchPath, string savedPath, bool reopen) =>
		// Built by hand (not JsonSerializer.Serialize, which is trim-unsafe — IL2026 — on the macOS target).
		_bridge.PostToWeb(
			$"{{\"type\":\"scratch-saved\",\"scratchPath\":{JsonString(scratchPath)},\"savedPath\":{JsonString(savedPath)},\"reopen\":{(reopen ? "true" : "false")}}}");

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

	/// <summary>Encodes a string as a JSON string literal (trim-safe; no reflection).</summary>
	private static string JsonString(string value) => "\"" + JsonEncodedText.Encode(value) + "\"";

	/// <summary>The correlation <c>id</c> of an fs-stat/read/write request.</summary>
	private static string FsId(JsonElement root) =>
		root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;

	/// <summary>The native <c>path</c> of an fs-stat/read/write request.</summary>
	private static string FsPath(JsonElement root) =>
		root.TryGetProperty("path", out var pathEl) ? pathEl.GetString() ?? string.Empty : string.Empty;

	/// <summary>Routes a terminal message to the controller for its <c>session</c> (default: claude).</summary>
	private TerminalController? TerminalFor(JsonElement root) {
		string? session = root.TryGetProperty("session", out var s) ? s.GetString() : null;
		return session == "shell" ? _shell : _claude;
	}
}
