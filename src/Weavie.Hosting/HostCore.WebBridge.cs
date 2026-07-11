using System.Text;
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

		// Backstop the dispatch: a malformed message (missing field, bad base64, wrong value kind) must not throw
		// out of the bridge callback and crash the host — especially the network-exposed headless/Runner worker.
		try {
			Dispatch(type, root, json);
		} catch (Exception ex) {
			Log($"[weavie] error handling '{type}': {ex.Message}");
		}
	}

	private static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadAgentInputAnswers(JsonElement root) {
		var answers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
		if (!root.TryGetProperty("answers", out var payload) || payload.ValueKind != JsonValueKind.Object) {
			return answers;
		}

		foreach (var property in payload.EnumerateObject()) {
			var values = new List<string>();
			if (property.Value.ValueKind == JsonValueKind.Array) {
				foreach (var value in property.Value.EnumerateArray()) {
					if (value.ValueKind == JsonValueKind.String && value.GetString() is { } text) {
						values.Add(text);
					}
				}
			}

			answers[property.Name] = values;
		}

		return answers;
	}

	/// <summary>Routes one parsed web message to its handler. Isolated from <see cref="OnWebMessage"/> so a throw is contained, not fatal.</summary>
	private void Dispatch(string type, JsonElement root, string json) {
		switch (type) {
			case "term-input":
				// Frozen from the moment an update restart commits: a keystroke forwarded now could start a
				// turn the restart would silently discard (see HostCore.Drain.cs).
				if (!_drainInputFrozen) {
					TerminalFor(root)?.Write(Convert.FromBase64String(root.GetProperty("dataB64").GetString() ?? string.Empty));
				}

				break;
			case "term-paste-image":
				HandlePasteImage(root);
				break;
			case "agent-attachment-upload":
				HandleAgentAttachmentUpload(root);
				break;
			case "agent-attachment-remove":
				HandleAgentAttachmentRemove(root);
				break;
			case "term-resize": {
					int cols = root.GetProperty("cols").GetInt32();
					int rows = root.GetProperty("rows").GetInt32();
					TerminalFor(root)?.Resize(cols, rows);
					// Seed for the next restart: only the shell replays a raw scrollback log, so only its real size must
					// survive. term-resize is active-pane-only, so this never records the 80×24 a hidden pane reports.
					if (root.GetStringOrEmpty("session") == "shell") {
						_sessionStore.RecordShellSize(cols, rows);
					}

					break;
				}
			case "term-ready":
				TerminalFor(root)?.OnReady(root.GetProperty("cols").GetInt32(), root.GetProperty("rows").GetInt32());
				break;
			case "agent-submit":
				HandleAgentSubmit(root);
				break;
			case "agent-interrupt":
				SessionForSlot(root)?.Agent.Structured?.Interrupt();
				break;
			case "agent-set-control":
				SessionForSlot(root)?.Agent.Controls?.SetControl(
					root.GetStringOrEmpty("axis"),
					root.GetStringOrEmpty("value"));
				break;
			case "agent-approval":
				SessionForSlot(root)?.Agent.Structured?.ResolveApproval(
					root.GetStringOrEmpty("requestId"),
					root.GetStringOrEmpty("decision"));
				break;
			case "agent-input":
				SessionForSlot(root)?.Agent.Structured?.ResolveInput(
					root.GetStringOrEmpty("requestId"),
					ReadAgentInputAnswers(root));
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
					_ = CreateSessionFromWebAsync(branch, resolvedBase, existing, root.GetStringOrNull("agentProviderId"));
					break;
				}

			case "dismiss-suggestion":
				DismissSuggestion(root.GetStringOrEmpty("id"), root.GetBoolOrFalse("forever"));
				break;

			case "list-branches": {
					_ = ListBranchesForWebAsync(root.GetStringOrEmpty("id"));
					break;
				}

			case "list-prs": {
					_ = ListPullRequestsForWebAsync(root.GetStringOrEmpty("id"), root.GetStringOrEmpty("query"));
					break;
				}

			case "open-pr": {
					_ = OpenPullRequestFromWebAsync(
						JsonInt(root, "number"),
						root.GetStringOrEmpty("owner"),
						root.GetStringOrEmpty("repo"));
					break;
				}

			case "resolve-pr": {
					_ = GetPullRequestForWebAsync(
						root.GetStringOrEmpty("id"),
						JsonInt(root, "number"),
						root.GetStringOrEmpty("owner"),
						root.GetStringOrEmpty("repo"));
					break;
				}

			case "diff-against": {
					_ = DiffAgainstFromWebAsync(root.GetStringOrEmpty("ref"));
					break;
				}

			case "connect-notion":
				PromptConnectNotion();
				break;
			case "set-source-token":
				_ = SaveSourceTokenAsync(root.GetStringOrEmpty("id"), root.GetStringOrEmpty("sourceId"), root.GetStringOrEmpty("token"));
				break;
			case "open-target":
				// The open resolver: the host matches the URL to a source (render natively) or replies open-web.
				OpenTargetForWeb(root.GetStringOrEmpty("url"));
				break;
			case "source-save-edit":
				_ = SaveSourceEditAsync(root.GetStringOrEmpty("target"), root.GetStringOrEmpty("oldStr"), root.GetStringOrEmpty("newStr"));
				break;

			case "add-pr-comment": {
					_ = AddPrCommentFromWebAsync(
						JsonInt(root, "number"),
						root.GetStringOrEmpty("path"),
						JsonInt(root, "line"),
						root.GetStringOrEmpty("side"),
						root.TryGetProperty("inReplyTo", out var irt) && irt.ValueKind == JsonValueKind.Number ? irt.GetInt64() : 0,
						root.GetStringOrEmpty("body"));
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
			case "clipboard-write":
				// Terminal copy / OSC 52 -> write to the OS clipboard (host-owned, dodging the WebView's focus gate).
				_platform.WriteClipboard(root.GetStringOrEmpty("text"));
				break;
			case "clipboard-read":
				// Terminal paste -> read the OS clipboard and reply, correlated by id (the fs-read pattern). Runs on
				// the UI thread (OnWebMessage's thread), where the native clipboard APIs are valid.
				_bridge.PostToWeb(
					$"{{\"type\":\"clipboard-content\",\"id\":{JsonString(root.GetStringOrEmpty("id"))},\"text\":{JsonString(_platform.ReadClipboard())}}}");
				break;
			case "clipboard-read-image":
				// Claude-pane paste in a native WebView -> read the OS clipboard as an image and reply, correlated by
				// id. An empty mime means no image, and the web falls back to a text paste. Same UI-thread seam.
				var clip = _platform.ReadClipboardImage();
				_bridge.PostToWeb(
					$"{{\"type\":\"clipboard-image-content\",\"id\":{JsonString(root.GetStringOrEmpty("id"))},\"mime\":{JsonString(clip.Mime)},\"dataB64\":{JsonString(Convert.ToBase64String(clip.Bytes))}}}");
				break;
			case "open-url":
				// A terminal hyperlink / Claude's auth URL -> open in the OS default browser. Allowlist http(s) at
				// this trust boundary: terminal content is untrusted, so the OS opener must never be reachable with a
				// file://, UNC path, or custom scheme (a ShellExecute / handler RCE vector). The web filters too, but
				// this is the authoritative gate — never trust the renderer alone.
				string openUrl = root.GetStringOrEmpty("url");
				if (IsHttpUrl(openUrl)) {
					_platform.OpenExternalUrl(openUrl);
				} else {
					Log($"[weavie] open-url refused (not http/https): {openUrl}");
				}

				break;
			case "term-cwd":
				// The shell child reported its cwd (OSC 7); remember it so a reopen relaunches there.
				TerminalFor(root)?.OnCwdReported(root.GetStringOrEmpty("cwd"));
				break;
			case "lsp-start":
				// The page opened a language client: spawn its server on the owning session, bound to the page-minted
				// channel. Routed by slot like the terminal, so a background session's client reaches its own servers.
				SessionForSlot(root)?.Lsp.Start(root.GetStringOrEmpty("slot"), root.GetStringOrEmpty("server"), root.GetStringOrEmpty("channel"));
				break;
			case "lsp-data":
				// One JSON-RPC payload from a language client → its server's stdin (the payload rides embedded, already JSON).
				SessionForSlot(root)?.Lsp.Data(root.GetStringOrEmpty("channel"), LspPayloadBytes(root));
				break;
			case "lsp-stop":
				// The page tore a language client down (document closed / session switch) → kill its server.
				SessionForSlot(root)?.Lsp.Stop(root.GetStringOrEmpty("channel"));
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
				// A review step-in: render the file's diff, plus its comments when the active review is a PR (the
				// web cleared them on the switch out, so re-push so the threads + Comment button reappear).
				PushReviewFileToWeb(root.GetProperty("path").GetString() ?? string.Empty);
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
			case "fs-read-bytes":
				// Raw bytes (base64) for the media pane — same path-routing + confinement as fs-read.
				if (ResolveFsSession(FsPath(root)) is { } readBytesSession) {
					_bridge.PostToWeb(readBytesSession.FileProvider.ReadBytes(FsId(root), FsPath(root)));
				} else {
					_bridge.PostToWeb(FileProviderProtocol.ReadBytesNotFound(FsId(root)));
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
			case "keep-hunk":
				KeepHunk(root);
				break;
			case "unkeep-hunk":
				UnkeepHunk(root);
				break;
			case "revert-file":
				RevertFile(root);
				break;
			case "keep-file":
				KeepFile(root);
				break;
			case "review-undo":
				ReviewUndo(root);
				break;
			case "review-redo":
				ReviewRedo();
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
				PushFileIndexToWeb(invalidate: false);
				break;
			case "find-in-files":
				_ = SearchInFilesAsync(root.GetStringOrEmpty("query"));
				break;
			case "ready":
				// The page's bridge listener is live; push the persisted state to restore it. Must go on `ready`, not
				// init — PostToWeb before navigation no-ops (window.__weavieReceive doesn't exist yet).
				// Build identity first: a tab reconnecting to a worker updated under it compares this against its
				// boot-time __WEAVIE_SHELL__.buildNumber and reloads itself to pick up the matching assets.
				_bridge.PostToWeb($"{{\"type\":\"host-info\",\"buildNumber\":{JsonString(BuildNumber)}}}");
				PushLayoutToWeb();
				// The ACTIVE session's editor tabs — normally the primary, but a restored worktree session may be
				// active after a reopen/update restart, and the page must open its tabs, not the primary's.
				if (_session is { } editorSession) {
					PushSessionEditorToWeb(editorSession);
				}

				PushRecentFilesToWeb();
				PushSessionList();
				PushGitStatus();
				PushRefLinkBase();
				PushRemoteAgentsToWeb();
				PushRailStateToWeb();
				// Re-advertise the active session's LSP catalog so a reconnect (a remote bridge drop, a refresh)
				// rebinds language clients on fresh channels — the resync the remote path needs. Background backends
				// are suppressed page-side, so only the active backend rebinds. See docs/specs/lsp-over-bridge.md.
				if (_session is { } lspSession) {
					PushLspConfigToWeb(lspSession);
				}

				// Re-seed the test profile so a reconnecting tab's run lenses reflect any change made while it was down.
				PushTestProfileToWeb();

				// Terminal output posted while the link was down never reached the page: re-sync every loaded
				// session's panes (replay the shell's log, nudge claude's TUI) — see TerminalController.ResyncPane.
				foreach (var slot in _sessions?.Slots ?? []) {
					slot.Session?.Claude?.ResyncPane();
					slot.Session?.Agent.ReplayPane();
					slot.Session?.Agent.ReplayControls();
					slot.Session?.Shell.ResyncPane();
				}

				_suggestions?.PushCurrent();
				// The built-in auto-config toast is emitted before any page exists (the probe runs during startup);
				// release it now that a page can render it. Fires once, whichever of write/ready landed second.
				MarkAutoConfigPageReady();
				// A tab that (re)connects mid-drain must learn the pending/restarting update state it missed.
				PushDrainStateToWeb();
				// A prior run that died on an unhandled exception left a crash report; surface it once, now that
				// the page can render the toast, so a silent hard exit doesn't go unnoticed.
				SurfacePriorCrash();
				// A settings.toml that was already malformed at boot never raised MalformedChanged, so surface it
				// now that the page can render the toast.
				if (_settings.IsMalformed) {
					NotifySettingsMalformed(true);
				}

				// Likewise, unknown command ids present in keybindings.json at boot raised no change event.
				NotifyUnknownKeybindingCommands(_keybindings.UnknownCommands);
				if (_keybindings.IsMalformed) {
					NotifyKeybindingsMalformed(true);
				}

				Ready?.Invoke();
				Log($"[weavie] {json}");
				break;
			case "monaco-ready":
				// The editor pane can now render inline decorations. Replay the review set AND the active session's
				// held openDiff here (not on `ready`, which fires before the editor exists) so changes/proposals that
				// landed before this connect — a reload, or a claude turn that beat the editor's init — surface
				// deterministically, with no settle sleep.
				PushReviewStateToWeb();
				_session?.EditorChannel.Replay();
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
			case "save-scratch-named":
				// Ctrl+S on a scratch buffer on a browser-served host (no native dialog): the web's in-app prompt
				// chose a workspace-relative name; resolve it under the workspace, write it, drop the temp.
				SaveScratchNamed(root);
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
			case "set-last-agent-provider":
				_railState.SetLastAgentProvider(root.GetStringOrEmpty("providerId"));
				break;
			case "set-promoted":
				// The page's promoted-remote-session set changed; persist the full set it sent.
				_railState.SetPromoted(StringArray(root, "promoted"));
				break;
			case "log":
				// A web-side log() (e.g. an editor-init failure): route it into the captured console stream, tagged,
				// so it lands in the in-app log viewer as a readable line rather than raw JSON.
				Log($"[web:{root.GetStringOrEmpty("level")}] {root.GetStringOrEmpty("message")}");
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
			lastAgentProvider = RememberedNewSessionProvider(),
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

	/// <summary>
	/// Re-points the page's language clients at a session's own LSP bridge on a switch (each session has its own,
	/// rooted at its worktree). Without it a switched-in worktree session's hover/diagnostics/go-to-def would keep
	/// resolving against the primary's checkout (whose config is the one injected at launch).
	/// </summary>
	private void PushLspConfigToWeb(HostSession session) =>
		_bridge.PostToWeb($"{{\"type\":\"lsp-config\",\"config\":{session.LspConfigJson}}}");

	/// <summary>
	/// Re-walks the active session's worktree and pushes its <c>file-index</c> so the omnibar's "Go to File" and
	/// the file browser re-root to it. When <paramref name="invalidate"/> (a session switch), a pending (empty)
	/// index posts first, in order with the switch's message train, so the page drops the outgoing session's
	/// files immediately — during the walk the omnibar must show nothing rather than offer a stale file whose
	/// path routes into the wrong worktree. A same-root refresh (the omnibar's open) keeps the current index
	/// usable while the walk runs. The walk runs off the UI thread; its result is guarded + posted back on it,
	/// so a slow walk from a stale session can't clobber the page's index after a further switch.
	/// </summary>
	private void PushFileIndexToWeb(bool invalidate) {
		if (_session is not { } session) {
			return;
		}

		if (invalidate) {
			_bridge.PostToWeb(ShellProtocol.BuildFileIndexPending(session.FileIndex.Root));
		}

		_ = Task.Run(async () => {
			var files = await GitTrackedFilesAsync(session.FileIndex.Root).ConfigureAwait(false)
				?? session.FileIndex.List();
			_ui.Post(() => {
				if (ReferenceEquals(_session, session)) {
					_bridge.PostToWeb(ShellProtocol.BuildFileIndex(session.FileIndex.Root, files));
				}
			});
		});
	}

	/// <summary>
	/// Runs the find-in-files content search over the active session's worktree (<c>git grep</c>) and pushes the
	/// matches (each with a canonical absolute path so a click reuses an open tab). The query is echoed back so the
	/// page can drop a stale reply for a query the user has typed past; an empty query clears results without git.
	/// A git failure sets <c>error</c> so the panel says the search failed rather than reporting it as no matches.
	/// </summary>
	private async Task SearchInFilesAsync(string query) {
		if (_session is not { } session) {
			return;
		}

		string root = session.WorkspaceRoot;
		var matches = new List<object>();
		bool truncated = false;
		string? error = null;
		if (query.Length > 0) {
			try {
				var result = await new GitService().GrepAsync(root, query, CancellationToken.None).ConfigureAwait(false);
				truncated = result.Truncated;
				foreach (var m in result.Matches) {
					matches.Add(new {
						path = WorkspacePaths.CanonicalFsPath(Path.GetFullPath(Path.Combine(root, m.Path))),
						line = m.Line,
						preview = m.Preview,
					});
				}
			} catch (GitException ex) {
				error = ex.Message;
				Log($"[weavie] find-in-files failed: {ex.Message}");
			}
		}

		// Guard + post on the UI thread, so a slow grep can't check active, lose to a switch, and still post.
		_ui.Post(() => {
			if (ReferenceEquals(_session, session)) {
				_bridge.PostToWeb(JsonSerializer.Serialize(new { type = "find-in-files-results", query, matches, truncated, error }));
			}
		});
	}

	/// <summary>
	/// The workspace's files as git sees them (tracked + untracked, .gitignore honored), as canonical absolute
	/// paths sorted like the plain index — so a gitignored file (a build artifact, a secret) never surfaces in
	/// Go-to-File. Null when the root isn't a git repo, so the caller falls back to the unfiltered walk.
	/// </summary>
	private static async Task<IReadOnlyList<string>?> GitTrackedFilesAsync(string root) {
		var relative = await new GitService().ListWorkspaceFilesAsync(root).ConfigureAwait(false);
		if (relative is null) {
			return null;
		}

		var absolute = new List<string>(relative.Count);
		foreach (string rel in relative) {
			absolute.Add(WorkspacePaths.CanonicalFsPath(Path.GetFullPath(Path.Combine(root, rel))));
		}

		absolute.Sort(StringComparer.OrdinalIgnoreCase);
		return absolute;
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
		_bridge.PostToWeb(ShellProtocol.BuildRecentFiles(_recentFiles.Top(RecentFilesPushCount, DateTime.UtcNow.Ticks)));

	/// <summary>
	/// Pushes the per-turn change list (each changed file + its first-change line) for the page's review walk +
	/// parked navigator. Driven by the change tracker, which records edits in every permission mode, so it's the
	/// review surface in default mode too. The page surfaces the review itself (parked, editor untouched) — the
	/// host no longer decides an auto-open.
	/// </summary>
	private void PushTurnChangesToWeb() {
		if (_session is { } session) {
			// The label names an armed PR/ref review ("PR #12", "vs main") in the navigator subtitle; empty for a
			// plain post-turn review. It rides the change list, so it survives switches (the tracker + review both
			// persist on the session) and is threaded here once — the sole builder of the turn-changes message.
			_bridge.PostToWeb(ChangeMessages.TurnChanges(session.Changes, ActiveReview()?.Label ?? string.Empty));
		}
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
	/// stale ← / → walk + parked navigator).
	/// </summary>
	private void PushIncomingReviewState() {
		if (_session is null) {
			return;
		}

		PushTurnChangesToWeb(); // threads the incoming session's review label (a PR/ref review follows its session)
		PushReviewHistoryToWeb(); // the incoming session's undo history persists — reflect it on the toolbar
	}

	/// <summary>
	/// Replays the active session's full review set to a just-connected page: the changed-file list (navigator),
	/// each file's inline diff, and the undo/redo history. The live turn pushes only reach an already-connected
	/// client, so without this a page that connects after the changes landed — a reload, or a slow first connect —
	/// shows no review surface until a session switch. A no-op when nothing is pending.
	/// </summary>
	private void PushReviewStateToWeb() {
		if (_session is not { } session) {
			return;
		}

		PushTurnChangesToWeb();
		foreach (var change in session.Changes.TurnChanges()) {
			PushReviewFileToWeb(change.Path); // includes PR comments so a reload restores the threads too
		}

		PushReviewHistoryToWeb();
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
		if (_session is not { } session) {
			return;
		}

		session.Changes.AcceptTurn();
		// Keep-all commits the board: a local "diff against" review is done, so drop it — else its label would
		// cling to the next plain turn. A PR review persists (its identity + comments outlive an equal tree).
		if (ActiveReview() is { PrNumber: 0 }) {
			_diffReviews.TryRemove(session.WorkspaceRoot, out _);
		}

		_bridge.PostToWeb(ChangeMessages.TurnReset());
		PushTurnChangesToWeb();
		PushReviewHistoryToWeb(); // keep-all is the commit point — the undo history reset
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

		// Workspace-guard every file before touching disk: one path outside the worktree aborts the whole revert.
		foreach (var change in session.Changes.TurnChanges()) {
			if (!BufferStore.IsWithinWorkspace(session.WorkspaceRoot, change.Path)) {
				Notify("warn", $"Couldn't revert {Path.GetFileName(change.Path)}: path is outside the workspace.");
				return;
			}
		}

		try {
			ApplyHistoryResult(session.Changes.RevertAll()); // one undoable step for the whole set
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Notify("warn", $"Couldn't revert all changes: {ex.Message}");
		}
	}

	/// <summary>
	/// Undoes a review action: <c>kind</c> "keep"/"revert" drives the type-split chords, an absent kind the
	/// toolbar's generic Undo. A blocked undo (a newer edit moved the file) toasts; otherwise the editor refreshes.
	/// </summary>
	private void ReviewUndo(JsonElement root) {
		if (_session is not { } session) {
			return;
		}

		string? kind = root.GetStringOrNull("kind");
		var result = kind switch {
			"keep" => session.Changes.UndoLastKeep(),
			"revert" => session.Changes.UndoLastRevert(),
			_ => session.Changes.UndoLast(),
		};
		HandleHistory(result);
		RevealHistoryChange(session, result);
	}

	/// <summary>Redoes the most recently undone review action (the toolbar/palette Redo).</summary>
	private void ReviewRedo() {
		if (_session is { } session) {
			var result = session.Changes.Redo();
			HandleHistory(result);
			RevealHistoryChange(session, result);
		}
	}

	/// <summary>
	/// After an undo/redo brings a change back, land the editor on it: open the first affected file at its first
	/// pending hunk, so the keystroke visibly takes you to what changed instead of re-rendering in place. No-op
	/// when the action left nothing pending (e.g. an undo that re-kept every hunk).
	/// </summary>
	private static void RevealHistoryChange(HostSession session, ReviewHistoryResult result) {
		if (!result.Acted) {
			return;
		}

		foreach (string path in result.Paths) {
			if (session.Changes.GetTurn(path) is { } turn
				&& LineDiff.FirstChangedLine(turn.BaselineText, turn.CurrentText) is { } line) {
				session.FileOpener.Open(path, line, preview: true, scratch: false);
				return;
			}
		}
	}

	/// <summary>
	/// Applies an undo/redo outcome: a blocked result (a newer edit is in the way) toasts and re-pushes
	/// availability; an action that ran refreshes each affected file and the review set via <see cref="ApplyHistoryResult"/>.
	/// </summary>
	private void HandleHistory(ReviewHistoryResult result) {
		if (result.Acted) {
			ApplyHistoryResult(result);
			return;
		}

		if (result.WasBlocked) {
			Notify("warn", "That change moved since — re-open to review before undoing it.");
		}

		PushReviewHistoryToWeb();
	}

	/// <summary>
	/// Pushes the editor refreshes for a completed undo/redo or revert-all: a deletion for a path the op removed,
	/// else a reload (when it touched disk) plus a fresh per-file diff. Then re-emits the review set + history state.
	/// </summary>
	private void ApplyHistoryResult(ReviewHistoryResult result) {
		if (_session is not { } session) {
			return;
		}

		foreach (string path in result.Paths) {
			if (session.Changes.GetTurn(path) is null) {
				PushDeletionToWeb(path); // the op removed (or re-removed) the file
			} else {
				if (result.TouchedDisk) {
					PushRefreshToWeb(path); // a revert/undo-of-revert rewrote disk — reload the model
				}

				PushTurnDiffToWeb(path);
			}
		}

		PushTurnChangesToWeb();
		PushReviewHistoryToWeb();
	}

	/// <summary>Pushes the review undo/redo availability so the page enables its Undo/Redo affordances.</summary>
	private void PushReviewHistoryToWeb() {
		if (_session is { } session) {
			_bridge.PostToWeb(ChangeMessages.ReviewHistory(session.Changes));
		}
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
			Notify("warn", $"Couldn't revert {Path.GetFileName(path)}: path is outside the workspace.");
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
			PushReviewHistoryToWeb(); // a revert is now undoable
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Notify("warn", $"Couldn't revert {Path.GetFileName(path)}: {ex.Message}");
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
			Notify("warn", $"Couldn't revert {Path.GetFileName(path)}: path is outside the workspace.");
			return;
		}

		try {
			PushAfterRevert(path, session.Changes.RevertFile(path));
			PushTurnChangesToWeb(); // the file has left the review set
			PushReviewHistoryToWeb(); // a revert is now undoable
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Notify("warn", $"Couldn't revert {Path.GetFileName(path)}: {ex.Message}");
		}
	}

	/// <summary>
	/// Keeps a single hunk: advances Core's review baseline over it (no disk write) so it drops from the pending
	/// diff for good and survives session switches. The web sends the same line ranges + <c>guardText</c> as a
	/// revert; a guard mismatch (a parallel edit moved the file) re-emits a fresh diff without advancing.
	/// </summary>
	private void KeepHunk(JsonElement root) {
		if (_session is not { } session) {
			return;
		}

		string path = root.GetStringOrEmpty("path");
		if (string.IsNullOrEmpty(path) || !BufferStore.IsWithinWorkspace(session.WorkspaceRoot, path)) {
			return;
		}

		var baselineRange = new LineRange(JsonInt(root, "baselineStart"), JsonInt(root, "baselineEndExclusive"));
		var currentRange = new LineRange(JsonInt(root, "currentStart"), JsonInt(root, "currentEndExclusive"));
		string guardText = root.GetStringOrEmpty("guardText");

		if (!session.Changes.KeepHunk(path, baselineRange, currentRange, guardText)) {
			Notify("warn", $"{Path.GetFileName(path)} changed — re-open to review.");
			PushTurnDiffToWeb(path); // re-render so the stale hunk geometry is replaced
			return;
		}

		PushTurnDiffToWeb(path);  // the inline diff drops the kept hunk
		PushTurnChangesToWeb();   // the file may have left the review set
		PushReviewHistoryToWeb(); // a keep is now undoable
	}

	/// <summary>
	/// Keeps a whole file: advances its review baseline to current (no disk write) so it leaves the review set for
	/// good — the file-scoped analogue of keep-all, sharing <see cref="SessionChangeTracker.KeepFile"/>.
	/// </summary>
	private void KeepFile(JsonElement root) {
		if (_session is not { } session) {
			return;
		}

		string path = root.GetStringOrEmpty("path");
		if (string.IsNullOrEmpty(path) || !BufferStore.IsWithinWorkspace(session.WorkspaceRoot, path)) {
			return;
		}

		session.Changes.KeepFile(path);
		PushTurnDiffToWeb(path);  // every hunk is now faded-accepted (review baseline == current)
		PushTurnChangesToWeb();   // the file's pending count drops, but it stays (faded) until keep-all
		PushReviewHistoryToWeb(); // a keep is now undoable
	}

	/// <summary>
	/// Un-keeps a single faded (accepted) hunk: Core splices its accepted-anchor lines back into the review
	/// baseline, returning it to the bright pending band. The inverse of <see cref="KeepHunk"/>; the web sends the
	/// accepted-anchor + review-baseline ranges and both sides' guard snapshots (a mismatch — a concurrent keep
	/// moved the baseline, or a turn boundary committed the anchor — re-emits a fresh diff without un-keeping).
	/// No disk write.
	/// </summary>
	private void UnkeepHunk(JsonElement root) {
		if (_session is not { } session) {
			return;
		}

		string path = root.GetStringOrEmpty("path");
		if (string.IsNullOrEmpty(path) || !BufferStore.IsWithinWorkspace(session.WorkspaceRoot, path)) {
			return;
		}

		var acceptedRange = new LineRange(JsonInt(root, "acceptedStart"), JsonInt(root, "acceptedEndExclusive"));
		var reviewRange = new LineRange(JsonInt(root, "reviewStart"), JsonInt(root, "reviewEndExclusive"));
		string acceptedGuardText = root.GetStringOrEmpty("acceptedGuardText");
		string guardText = root.GetStringOrEmpty("guardText");

		if (!session.Changes.UnkeepHunk(path, acceptedRange, reviewRange, acceptedGuardText, guardText)) {
			Notify("warn", $"{Path.GetFileName(path)} changed — re-open to review.");
			PushTurnDiffToWeb(path); // re-render so the stale hunk geometry is replaced
			return;
		}

		PushTurnDiffToWeb(path); // the hunk moves from the faded band back to the bright pending band
		PushTurnChangesToWeb();  // its first-pending-line shifts (the file was already in the set)
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
	/// True only for an absolute http/https URL — the open-url gate. Terminal content is untrusted, so the OS
	/// opener accepts nothing else: never a <c>file://</c>, a UNC path, or a custom scheme that could launch a
	/// handler.
	/// </summary>
	private static bool IsHttpUrl(string url) =>
		Uri.TryCreate(url, UriKind.Absolute, out var uri)
		&& (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

	/// <summary>
	/// Runs a Core command on the active session from a native trigger (e.g. the macOS menu bar), the same path
	/// the web's <c>invoke-command</c> takes. Fire-and-forget.
	/// </summary>
	public void InvokeCommand(string id) => InvokeCommandFromWeb(id, null, null);

	/// <summary>Runs a Core command with JSON arguments on the active session (native-trigger overload).</summary>
	public void InvokeCommand(string id, string? argsJson) => InvokeCommandFromWeb(id, argsJson, null);

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
		_bridge.PostToWeb(ShellProtocol.BuildNotify(level, message));

	/// <summary>
	/// As <see cref="Notify(string,string)"/>, with a dedupe <paramref name="key"/>: a later toast carrying the
	/// same key replaces the live one in place (e.g. a "reloaded" info clearing a lingering "malformed" error).
	/// </summary>
	public void Notify(string level, string message, string key) =>
		_bridge.PostToWeb(ShellProtocol.BuildNotify(level, message, key));

	/// <summary>Dismisses the live toast carrying <paramref name="key"/> in the page (an in-flight spinner whose operation finished).</summary>
	public void ClearNotify(string key) =>
		_bridge.PostToWeb(ShellProtocol.BuildNotifyClear(key));

	/// <summary>
	/// Creates a session from the page's <c>new-session</c> request and surfaces any failure as a toast (the
	/// rail's "+" is fire-and-forget, so the error would otherwise only reach the log).
	/// </summary>
	private async Task CreateSessionFromWebAsync(string? branch, string? baseSpec, bool attachExisting, string? agentProviderId) {
		var result = await NewSessionAsync(
			new NewSessionRequest {
				Branch = branch,
				Base = baseSpec,
				AttachExisting = attachExisting,
				AgentProviderId = agentProviderId,
			}, CancellationToken.None).ConfigureAwait(false);
		if (!result.Ok) {
			Notify("warn", result.Error ?? "Couldn't create the session.");
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

		_bridge.PostToWeb(JsonSerializer.Serialize(new { type = "branches-result", id, branches }));
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

		// Any handler exception becomes a Failure the caller receives — a tokened invoke-command whose reply
		// never posts strands the web promise forever (the in-process native transport never disconnects to
		// fail it). This method is the reply guarantee, so it catches everything rather than a known subset.
		CommandResult result;
		try {
			result = await _session.Commands.InvokeAsync(id, argsJson, CancellationToken.None).ConfigureAwait(false);
		} catch (Exception ex) {
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
		string scratchPath = root.TryGetProperty("path", out var pEl) ? pEl.GetString() ?? string.Empty : string.Empty;
		if (_session is not { } session) {
			PostScratchSaved(scratchPath, string.Empty, reopen: false); // no active session — never leave Ctrl+S hanging
			return;
		}

		string content = root.TryGetProperty("content", out var cEl) ? cEl.GetString() ?? string.Empty : string.Empty;
		string suggested = root.TryGetProperty("suggestedName", out var nEl) ? nEl.GetString() ?? "Untitled" : "Untitled";

		try {
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
		} catch (Exception ex) {
			// Any unforeseen fault (the dialog, the temp delete) must still settle the web's await, or the
			// untitled tab is stuck and Ctrl+S looks dead.
			Notify("error", $"Couldn't save the file: {ex.Message}");
			PostScratchSaved(scratchPath, string.Empty, reopen: false);
		}
	}

	/// <summary>
	/// Saves a scratch buffer under an in-app-chosen workspace-relative <c>name</c> (browser-served host, no
	/// native dialog), resolved under the active session's worktree, then deletes the temp and replies
	/// <c>scratch-saved</c>. Rejects a name that escapes the workspace.
	/// </summary>
	private void SaveScratchNamed(JsonElement root) {
		string scratchPath = root.GetStringOrEmpty("path");
		if (_session is not { } session) {
			PostScratchSaved(scratchPath, string.Empty, reopen: false); // no active session — never leave Ctrl+S hanging
			return;
		}

		string name = root.GetStringOrEmpty("name").Trim();
		if (name.Length == 0) {
			PostScratchSaved(scratchPath, string.Empty, reopen: false);
			return;
		}

		string content = root.GetStringOrEmpty("content");
		string target = Path.GetFullPath(Path.Combine(Path.GetFullPath(session.WorkspaceRoot), name));
		if (!BufferStore.IsWithinWorkspace(session.WorkspaceRoot, target)) {
			Notify("error", $"Can't save outside the workspace: {name}");
			PostScratchSaved(scratchPath, string.Empty, reopen: false);
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
		PostScratchSaved(scratchPath, target, reopen: true);
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

	/// <summary>
	/// Resolves the session a message names by its <c>slot</c> (the terminal's routing, sans pane): a background
	/// session's work (LSP, a pasted image) must reach THAT session, not the active one. Falls back to the active
	/// session when the slot is absent or no longer loaded.
	/// </summary>
	private HostSession? SessionForSlot(JsonElement root) {
		string? slot = root.TryGetProperty("slot", out var sl) ? sl.GetString() : null;
		var session = !string.IsNullOrEmpty(slot) ? _sessions?.Find(slot)?.Session : null;
		return session ?? _session;
	}

	/// <summary>The embedded JSON-RPC <c>payload</c> of an <c>lsp-data</c> message as UTF-8 bytes (empty when absent).</summary>
	private static ReadOnlyMemory<byte> LspPayloadBytes(JsonElement root) =>
		root.TryGetProperty("payload", out var p) ? Encoding.UTF8.GetBytes(p.GetRawText()) : ReadOnlyMemory<byte>.Empty;
}
