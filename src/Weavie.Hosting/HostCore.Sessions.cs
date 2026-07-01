using System.Text.Json;
using Weavie.Core;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Git;
using Weavie.Core.Sessions;
using Weavie.Core.Theming;
using Weavie.Core.Workspaces;
using Weavie.Core.Worktrees;

namespace Weavie.Hosting;

// HostCore's ISessionHost impl + worktree/slot orchestration behind the rail: one SessionSlot per worktree
// (plus primary), each LOADED (live HostSession) or UNLOADED. The active session drives the page (pushes gated
// on IsActiveSession). See docs/specs/multi-session-and-worktrees.md.
public sealed partial class HostCore {
	/// <summary>
	/// Wires a session's command handlers + change/status/diff push subscriptions, gated on
	/// <see cref="IsActiveSession"/>. State that ACCUMULATES while muted (the review feed) must also be
	/// re-applied on switch-in (<c>PushIncomingReviewState</c> in <see cref="SwitchToSlot"/>).
	/// </summary>
	private void WireSession(HostSession session) {
		// Web commands drive the page's SINGLE editor surface, so only the active session may run them — else a
		// background Claude's web command would execute in the foreground session. Reject loudly (no misroute).
		session.Commands.WebInvoker = (id, args, ct) => IsActiveSession(session)
			? InvokeWebCommandAsync(id, args, ct)
			: Task.FromResult(CommandResult.Failure(
				$"Command '{id}' runs in the editor UI and can only run for the focused session; switch to it and retry."));
		session.Commands.RegisterHandler(CoreCommands.ReopenTerminal, (_, _) => {
			_ui.Post(() => session.Shell.Restart());
			return Task.FromResult(CommandResult.Success("Reopened the terminal."));
		});
		session.Commands.RegisterHandler(CoreCommands.RestartClaude, (_, _) => {
			_ui.Post(() => session.Claude.Restart());
			return Task.FromResult(CommandResult.Success("Restarted Claude."));
		});
		session.Commands.RegisterHandler(CoreCommands.ToggleWindow, (_, _) => {
			_ui.Post(_platform.ToggleWindow);
			return Task.FromResult(CommandResult.Success("Toggled the Weavie window."));
		});
		// Pre-fills the worktree-setup analysis prompt into the primary session's Claude (seeds whichever session
		// is active when the card is clicked; the handler always targets the primary).
		session.Commands.RegisterHandler(CoreCommands.SuggestSetupCommand, (_, _) => {
			_ui.Post(SeedSetupCommandPrompt);
			return Task.FromResult(CommandResult.Success("Asked Claude to suggest a worktree setup command."));
		});
		// Connect Notion: open the token page in the browser and ask the page to show the token input (the user
		// pastes it there; set-source-token validates + saves). Synchronous — the work happens on the page.
		session.Commands.RegisterHandler(CoreCommands.ConnectNotion, (_, _) => {
			PromptConnectNotion();
			return Task.FromResult(CommandResult.Success("Opening your browser to connect Notion…"));
		});
		ThemeCommands.RegisterHandlers(session.Commands, _settings, _themeOverrides, VsixPicker);
		FontCommands.RegisterHandlers(session.Commands, _settings);
		SessionCommands.RegisterHandlers(session.Commands, this);

		session.Changes.Changed += () => {
			if (IsActiveSession(session)) {
				PushTurnChangesToWeb();
			}
		};
		session.Changes.FileChanged += path => {
			if (IsActiveSession(session)) {
				PushRefreshToWeb(path);
				PushTurnDiffToWeb(path);
			}
		};
		session.Changes.FileDeleted += path => {
			if (IsActiveSession(session)) {
				PushDeletionToWeb(path);
			}
		};
		session.Status.Changed += status => {
			if (IsActiveSession(session)) {
				PostSessionStatus(status);
				// A turn settling may have changed files / the branch — refresh the footer's git status.
				PushGitStatus();
			}

			// The review set is pushed live on every edit (Changes.Changed), so the page's parked navigator
			// surfaces changes as they land — no status-driven re-push or auto-open arming needed here.
			_ui.Post(PushSessionList);
		};
		session.FileChanges += changes => {
			if (IsActiveSession(session)) {
				PushWatcherChangesToWeb(changes);
			}
		};
	}

	private bool IsActiveSession(HostSession session) => ReferenceEquals(_session, session);

	/// <summary>Test seam: the session currently driving the page (the active backend), or null before startup.</summary>
	internal HostSession? ActiveSessionForTest() => _session;

	/// <summary>Every loaded session's live backend (the active one plus any background slots), in rail order.</summary>
	private List<HostSession> LoadedSessions() {
		var list = new List<HostSession>();
		if (_sessions is not null) {
			foreach (var slot in _sessions.Slots) {
				if (slot.Session is { } session) {
					list.Add(session);
				}
			}
		} else if (_session is not null) {
			// Pre-rail (during StartAsync, before _sessions exists): only the primary is live.
			list.Add(_session);
		}

		return list;
	}

	/// <summary>
	/// The session owning <paramref name="path"/> for an <c>fs-stat</c>/<c>fs-read</c>/<c>fs-write</c>: the loaded
	/// session whose worktree contains it (longest-prefix), else the active session for a workspace scratch path,
	/// else <c>null</c>. Routing by path (not the active session) keeps a switch from losing the outgoing
	/// session's working-copy flush. A non-active owner is still served (data safety) but logged.
	/// </summary>
	private HostSession? ResolveFsSession(string path) {
		if (string.IsNullOrEmpty(path)) {
			return null;
		}

		var sessions = LoadedSessions();
		if (sessions.Count == 0) {
			return null;
		}

		int index = WorkspacePathRouter.OwningRootIndex([.. sessions.Select(s => s.WorkspaceRoot)], path);
		if (index >= 0) {
			var owner = sessions[index];
			if (!ReferenceEquals(owner, _session)) {
				Log($"[weavie] fs op for {path} routed to background session '{owner.Id}' (active '{_session?.Id}') — likely a stale editor tab");
			}

			return owner;
		}

		// A workspace scratch (untitled) buffer lives outside every worktree but is shared across the window's
		// sessions, so serve it from the active session (whose editor shows it).
		if (BufferStore.IsWithinWorkspace(WeaviePaths.WorkspaceScratchDir(Id), path)) {
			return _session;
		}

		Log($"[weavie] fs op refused: {path} is outside every session worktree and the scratch dir");
		return null;
	}

	/// <summary>
	/// Routes a <c>diff-resolved</c> by owning session (diff ids are process-unique): a switch mid-resolve must
	/// not hit another session's diff. Returns whether any session owned it (<c>false</c> = switch-race the caller logs).
	/// </summary>
	private bool ResolveDiff(string id, bool kept, string? finalContents) {
		foreach (var session in LoadedSessions()) {
			if (session.DiffPresenter.Resolve(id, kept, finalContents)) {
				return true;
			}
		}

		return false;
	}

	/// <summary>One git probe (instance reused downstream) for the rail label + worktree manager, so is-repo isn't
	/// run twice. Returns <c>IsRepo=false</c> when git is missing — the workspace still opens.</summary>
	private async Task<(GitService Git, bool IsRepo)> ProbeGitAsync() {
		var git = new GitService();
		try {
			return (git, await git.IsRepositoryAsync(WorkspaceRoot).ConfigureAwait(false));
		} catch (GitException) {
			return (git, false);
		}
	}

	/// <summary>Builds the workspace's worktree manager from the shared git probe. Caller guards on is-repo.</summary>
	private WorktreeManager BuildWorktreeManager(GitService git) {
		var registry = new WorktreeRegistry(new LocalFileSystem(), WeaviePaths.WorkspaceWorktreesFile(Id));
		registry.Log += line => Console.WriteLine($"[worktrees] {line}");

		// Runs worktree.setupCommand/teardownCommand around create/discard. The command strings are read
		// live from settings; progress + results surface as toasts (and full output to the console).
		var provisioner = new ShellWorktreeProvisioner(
			() => _settings.GetString("worktree.setupCommand"),
			() => _settings.GetString("worktree.teardownCommand"));
		provisioner.Starting += OnWorktreeCommandStarting;
		provisioner.Finished += OnWorktreeCommandFinished;
		_worktreeProvisioner = provisioner;

		var manager = new WorktreeManager(git, registry, WorkspaceRoot, WeaviePaths.WorkspaceWorktreesDir(Id), provisioner);
		manager.Log += line => Console.WriteLine($"[worktree] {line}");
		return manager;
	}

	/// <summary>Kicks off the worktree setup command in the background so the new session opens immediately;
	/// progress + failures surface via the provisioner's events. No-op when the workspace isn't a git repo.</summary>
	private void StartWorktreeSetup(string worktreePath) {
		if (_worktreeProvisioner is null) {
			return;
		}

		_ = Task.Run(async () => {
			try {
				// Not tied to the create command's lifetime — a returning command must not cancel the setup.
				await _worktreeProvisioner.RunSetupAsync(worktreePath, CancellationToken.None).ConfigureAwait(false);
			} catch (Exception ex) {
				Console.WriteLine($"[weavie] worktree setup command failed to run: {ex}");
				_ui.Post(() => Notify(
					"error", $"Worktree setup for '{WorktreeLabel(worktreePath)}' couldn't run: {ex.Message}"));
			}
		});
	}

	private void OnWorktreeCommandStarting(WorktreeCommandEvent e) {
		string label = WorktreeLabel(e.WorktreePath);
		string message = e.Phase == WorktreeCommandPhase.Setup
			? $"Setting up worktree '{label}'… ({e.Command})"
			: $"Cleaning up worktree '{label}'… ({e.Command})";
		_ui.Post(() => Notify("info", message));
	}

	private void OnWorktreeCommandFinished(WorktreeCommandEvent e) {
		var result = e.Result!;
		string label = WorktreeLabel(e.WorktreePath);
		string phase = e.Phase == WorktreeCommandPhase.Setup ? "setup" : "teardown";
		string output = string.Join(
			Environment.NewLine,
			new[] { result.StdOut, result.StdErr }.Where(s => !string.IsNullOrWhiteSpace(s)));
		if (output.Length > 0) {
			Console.WriteLine($"[worktree-{phase}] {e.Command} (exit {result.ExitCode}) in {e.WorktreePath}{Environment.NewLine}{output}");
		}

		_ui.Post(() => {
			if (result.Succeeded) {
				Notify("info", e.Phase == WorktreeCommandPhase.Setup
					? $"Worktree '{label}' is ready."
					: $"Worktree '{label}' cleaned up.");
			} else {
				Notify("error", $"Worktree {phase} command failed (exit {result.ExitCode}) — see console.");
			}
		});
	}

	private static string WorktreeLabel(string path) =>
		Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

	/// <summary>Creates and registers the primary (workspace-root) slot, already loaded with the primary session.</summary>
	private void AddPrimarySlot(string label) {
		var slot = new SessionSlot {
			Id = _primarySession!.Id,
			Label = label,
			WorktreePath = WorkspaceRoot,
			IsPrimary = true,
			Session = _primarySession,
			LastActiveUtc = DateTimeOffset.UtcNow,
		};
		slot.Session.BindTerminalsToSlot(slot.Id);
		_sessions?.Add(slot, activate: true);
	}

	/// <summary>
	/// Reconciles the worktree registry against real git, then adds an UNLOADED slot for every existing
	/// non-primary worktree so it appears on the rail (faded) instead of leaking invisibly. Orphans are skipped.
	/// </summary>
	private async Task ReconcileWorktreesOnOpenAsync() {
		if (_worktrees is null || _sessions is null) {
			return;
		}

		try {
			var report = await _worktrees.ReconcileAsync().ConfigureAwait(false);
			foreach (var status in report.Statuses) {
				if (!status.Exists || status.IsPrimary) {
					continue;
				}

				string label = status.Branch ?? Path.GetFileName(
					status.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
				if (_sessions.Find(label) is not null) {
					continue; // already surfaced
				}

				_sessions.Add(new SessionSlot {
					Id = label,
					Label = label,
					WorktreePath = status.Path,
					IsPrimary = false,
					Session = null,
				}, activate: false);
			}

			PushSessionList();
		} catch (GitException ex) {
			Console.WriteLine($"[weavie] worktree reconcile failed: {ex.Message}");
			Notify("warn", "Couldn't list existing worktrees — some sessions may not appear on the rail.");
		}
	}

	/// <summary>Pushes the session list (id, label, active, loaded, status, identity) to the page's rail.</summary>
	private void PushSessionList() {
		if (_sessions is null) {
			return;
		}

		// Loaded sessions first (creation order), dormant ones to the bottom — a parked session shouldn't sit
		// between two live ones. OrderByDescending is stable, so the always-loaded primary stays at the top.
		var sessions = _sessions.Slots
			.OrderByDescending(slot => slot.Loaded)
			.Select(slot => new {
				id = slot.Id,
				label = slot.Label,
				active = ReferenceEquals(_sessions.ActiveSlot, slot),
				loaded = slot.Loaded,
				primary = slot.IsPrimary,
				status = slot.Session is { } s ? StatusName(s.Status.Status) : "idle",
				hue = SessionIdentity.Hue(slot.Label),
				monogram = SessionIdentity.Monogram(slot.Label),
			});
		_bridge.PostToWeb(JsonSerializer.Serialize(new { type = "session-list", sessions }));
	}

	private async Task<string> ResolvePrimaryLabelAsync(GitService git, bool isRepo) {
		try {
			if (isRepo) {
				string? branch = await git.GetCurrentBranchAsync(WorkspaceRoot).ConfigureAwait(false);
				if (!string.IsNullOrWhiteSpace(branch)) {
					return branch;
				}
			}
		} catch (GitException) {
			// Branch read failed → fall back to the folder name for the rail label.
		}

		return Path.GetFileName(WorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
	}

	private void PostSessionStatus(SessionStatus status) =>
		_bridge.PostToWeb($"{{\"type\":\"session-status\",\"session\":\"claude\",\"status\":\"{StatusName(status)}\"}}");

	private static string StatusName(SessionStatus status) => status switch {
		SessionStatus.Starting => "starting",
		SessionStatus.Working => "working",
		SessionStatus.NeedsInput => "needsInput",
		SessionStatus.Idle => "idle",
		SessionStatus.Error => "error",
		_ => "idle",
	};

	/// <summary>Builds + wires a new <see cref="HostSession"/> rooted at <paramref name="cwd"/> (the live backend for a slot).</summary>
	private HostSession CreateSession(string cwd) {
		var session = new HostSession(
			_bridge, _settings, _layout, cwd, WeaviePaths.WorkspaceScratchDir(Id),
			Guid.NewGuid().ToString("n")[..8],
			_commandRegistry, _keybindings, _themeOverrides, _platform.PtyLauncher, _claudeSessions);
		// Persist the shell scrollback (keyed by worktree path, stable across reloads) so a reattaching client
		// replays a coherent screen. Shell only — claude resumes its own conversation.
		session.Shell.ScrollbackLogPath =
			WeaviePaths.WorkspaceTerminalLogFile(Id, WorkspaceId.ForPath(cwd).Value, "shell");
		WireSession(session);
		return session;
	}

	/// <summary>
	/// Brings up an unloaded slot's backend (builds + wires its HostSession), then always (re-)binds its
	/// terminals to the slot id — tagging this session's <c>term-output</c> with its rail id, without which the
	/// page can't match output to the xterm and the panes stay blank. Idempotent, so safe on a plain switch.
	/// </summary>
	private void LoadSlot(SessionSlot slot) {
		if (!slot.Loaded) {
			slot.Session = CreateSession(slot.WorktreePath);
		}

		slot.Session!.BindTerminalsToSlot(slot.Id);
	}

	/// <summary>
	/// Loads a dormant slot's backend in the background (the rail's "Load session"): creates its
	/// <see cref="HostSession"/> and starts its terminals so its Claude runs and reports status, WITHOUT binding
	/// the page to it — kept live so a later switch is instant. No-op if already loaded.
	/// </summary>
	private void LoadSlotInBackground(SessionSlot slot) {
		if (slot.Loaded) {
			return;
		}

		LoadSlot(slot);
		var session = slot.Session!;
		// Start the backends now so Claude runs even before its pane mounts (else it spawns on term-ready); the
		// resize nudge on first mount repaints the live TUI.
		session.Claude.EnsureStarted();
		session.Shell.EnsureStarted();
		slot.LastActiveUtc = DateTimeOffset.UtcNow;
		PushSessionList();
	}

	/// <summary>
	/// Binds the page to <paramref name="slot"/>, loading its backend first if dormant. Terminals are a pure
	/// show/hide (each loaded session keeps its own live xterm pair). Rebinds the single editor to the slot's
	/// worktree tabs, re-roots the omnibar/file browser, then re-pushes status + the rail + focus.
	/// </summary>
	private void SwitchToSlot(SessionSlot slot) {
		LoadSlot(slot);
		var session = slot.Session!;

		var previous = _session;
		if (previous is not null && !ReferenceEquals(previous, session)) {
			// Mute the outgoing session's editor output BEFORE the rebind: tears its live blocking diff out of the
			// page (re-renders on switch-back) so it can't linger over the incoming session.
			previous.SetEditorOutputActive(false);
		}

		_session = session;
		_sessions?.SetActive(slot);
		slot.LastActiveUtc = DateTimeOffset.UtcNow;

		// Tear down the outgoing session's inline review markers BEFORE the rebind + the incoming channel's
		// held-openDiff replay — the reset's clearAll wipes the whole inline-diff registry, so running it after the
		// replay would erase a just-rendered background openDiff (it shares that registry).
		ResetReviewMarkers();
		// Rebind the editor to this session's worktree: push its open tabs so the page closes the previous
		// session's working copies and reopens this one's.
		PushSessionEditorToWeb(session);
		// Unmute the incoming session's editor output AFTER the rebind, so work it held while muted (a background
		// openDiff, files Claude opened) replays onto the rebound editor instead of being wiped by the rebind.
		session.SetEditorOutputActive(true);
		// Re-root the omnibar quick-open + file browser to this session's worktree.
		PushFileIndexToWeb();
		// Re-point the editor's language clients at this session's own LSP bridge (rooted at its worktree).
		PushLspConfigToWeb(session);
		PostSessionStatus(session.Status.Status);
		// Push the incoming session's worktree branch to the footer (its worktree may be on a different branch).
		PushGitStatus();
		// Catch the page up on the incoming session's inline turn-review: its muted-while-background diffs (and
		// the ← / → walk) are gated on the active session, so without this they wouldn't appear and the previous
		// session's walk would linger. Pushed after the status so the web's auto-arm sees the idle state.
		PushIncomingReviewState();
		// If the incoming session is a PR review, re-push its changed-file list so the diff navigator follows it.
		PushActivePrChanges();
		// The rail push carries the new active flag, flipping the page to this session's (already-live) terminal
		// panes. Pushed before focus so the target pane is shown first.
		PushSessionList();
		// Land keyboard focus in the new session's Claude pane.
		_bridge.PostToWeb("{\"type\":\"focus-pane\",\"kind\":\"terminal:claude\"}");
	}

	/// <summary>
	/// Pushes a session's open editor tabs (a <c>set-editor-session</c>) so the page rebinds the editor to that
	/// session's worktree files, built from its in-memory <see cref="HostSession.EditorSession"/> (missing files dropped).
	/// </summary>
	private void PushSessionEditorToWeb(HostSession session) =>
		_bridge.PostToWeb(EditorSessionStore.BuildRestoreJson(
			session.EditorSession, session.FileSystem, session.WorkspaceRoot, session.Id, Log));

	/// <inheritdoc/>
	public Task<CommandResult> NewSessionAsync(NewSessionRequest request, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(request);
		if (request.AttachExisting) {
			return AttachExistingSessionAsync(request.Branch, request.Prompt, ct);
		}

		return CreateWorktreeSessionAsync(request.Branch, request.Base, request.Prompt, ct);
	}

	/// <inheritdoc/>
	public Task<CommandResult> ForkSessionAsync(ForkSessionRequest request, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(request);
		// A new worktree off the current session's HEAD, seeded with the handoff brief (PTY-injected).
		return CreateWorktreeSessionAsync(request.Branch, "current", request.Handoff, ct);
	}

	/// <inheritdoc/>
	public Task<CommandResult> LoadSessionAsync(string? sessionId, CancellationToken ct) {
		if (string.IsNullOrWhiteSpace(sessionId)) {
			return Task.FromResult(CommandResult.Failure("Load needs a session id; the active session is already loaded."));
		}

		var target = _sessions?.Find(sessionId);
		if (target is null) {
			return Task.FromResult(CommandResult.Failure("No such session."));
		}

		if (target.Loaded) {
			return Task.FromResult(CommandResult.Success("That session is already loaded."));
		}

		var result = new TaskCompletionSource<CommandResult>();
		_ui.Post(() => {
			try {
				LoadSlotInBackground(target);
				result.SetResult(CommandResult.Success($"Loaded session '{target.Label}' in the background."));
			} catch (Exception ex) {
				result.SetException(ex);
			}
		});
		return result.Task;
	}

	/// <inheritdoc/>
	public Task<CommandResult> UnloadSessionAsync(string? sessionId, CancellationToken ct) {
		var target = string.IsNullOrWhiteSpace(sessionId) ? _sessions?.ActiveSlot : _sessions?.Find(sessionId);
		if (target is null) {
			return Task.FromResult(CommandResult.Failure("No such session."));
		}

		if (target.IsPrimary) {
			return Task.FromResult(CommandResult.Failure("The primary session can't be unloaded; close the window instead."));
		}

		if (!target.Loaded) {
			return Task.FromResult(CommandResult.Success("That session is already unloaded."));
		}

		var result = new TaskCompletionSource<CommandResult>();
		_ui.Post(async () => {
			try {
				await UnloadSlotAsync(target).ConfigureAwait(false);
				result.SetResult(CommandResult.Success("Unloaded the session (its worktree is kept; click the chip to reload)."));
			} catch (Exception ex) {
				result.SetException(ex);
			}
		});
		return result.Task;
	}

	/// <inheritdoc/>
	public Task<CommandResult> DeleteSessionAsync(string? sessionId, bool force, CancellationToken ct) {
		var target = string.IsNullOrWhiteSpace(sessionId) ? _sessions?.ActiveSlot : _sessions?.Find(sessionId);
		if (target is null) {
			return Task.FromResult(CommandResult.Failure("No such session."));
		}

		if (target.IsPrimary) {
			return Task.FromResult(CommandResult.Failure("The primary session can't be deleted; close the window instead."));
		}

		if (_worktrees is not { } worktrees) {
			return Task.FromResult(CommandResult.Failure("This workspace isn't a git repository, so it has no worktree to delete."));
		}

		string worktreePath = target.WorktreePath;
		string label = target.Label;

		var result = new TaskCompletionSource<CommandResult>();
		_ui.Post(async () => {
			try {
				// Check for uncommitted work BEFORE tearing anything down, so a blocked delete leaves the session
				// untouched rather than unloading it as a side effect. Skip when the worktree is gone/half-removed
				// (no .git) — nothing left to lose, and git can't answer git status there.
				if (!force && IsLiveWorktree(worktreePath)
					&& await new GitService().HasUncommittedChangesAsync(worktreePath, ct).ConfigureAwait(false)) {
					result.SetResult(CommandResult.Failure(
						$"Session '{label}' has uncommitted changes; deleting would discard them. Re-run with force to delete anyway."));
					return;
				}

				// Tear the live backend down first so no process holds the worktree dir, then remove the worktree
				// (keeping the branch). Past the dirty guard the deletion runs under CancellationToken.None, NOT
				// `ct`: when Claude deletes its own session, UnloadSlotAsync disposes the IDE-MCP server handling
				// this call, cancelling `ct` — which would crash git mid-delete and orphan the worktree.
				if (target.Loaded) {
					await UnloadSlotAsync(target).ConfigureAwait(false);
				}

				// Settle before removal: Windows can lag on releasing the unloaded children's handles, and external
				// scanners may briefly hold a lock. A short pause lets git's one-shot remove succeed instead of
				// partial-failing and orphaning the directory (git deletes its own record mid-failure, unrecoverable).
				await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
				await worktrees.RemoveAsync(worktreePath, deleteBranch: false, force, CancellationToken.None).ConfigureAwait(false);
				_sessions?.Remove(target);
				PushSessionList();
				result.SetResult(CommandResult.Success($"Deleted session '{label}': its worktree was removed and the branch kept."));
			} catch (WorktreeDirtyException) {
				result.SetResult(CommandResult.Failure(
					$"Session '{label}' has uncommitted changes; deleting would discard them. Re-run with force to delete anyway."));
			} catch (WorktreeOrphanException ex) {
				result.SetResult(CommandResult.Failure($"Couldn't delete session '{label}': {ex.Message}"));
			} catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException) {
				result.SetResult(CommandResult.Failure($"Couldn't delete session '{label}': {ex.Message}"));
			} catch (Exception ex) {
				// Posted as async-void onto the UI thread: an escaping exception crashes the app instead of
				// failing the command, so funnel anything unexpected back to the awaiting caller.
				result.SetException(ex);
			}
		});
		return result.Task;
	}

	/// <inheritdoc/>
	public async Task<CommandResult> ClassifyDeleteAsync(string? sessionId, CancellationToken ct) {
		var target = string.IsNullOrWhiteSpace(sessionId) ? _sessions?.ActiveSlot : _sessions?.Find(sessionId);
		if (target is null) {
			return CommandResult.Failure("No such session.");
		}

		if (target.IsPrimary) {
			return CommandResult.Failure("The primary session can't be deleted; close the window instead.");
		}

		// A gone/half-removed worktree (no .git) can't be inspected and has nothing left to lose — classify clean.
		string state = "clean";
		if (IsLiveWorktree(target.WorktreePath)) {
			try {
				state = await new GitService().GetChangeStateAsync(target.WorktreePath, ct).ConfigureAwait(false) switch {
					WorktreeChangeState.UntrackedOnly => "untracked",
					WorktreeChangeState.Modified => "modified",
					_ => "clean",
				};
			} catch (GitException ex) {
				return CommandResult.Failure($"Couldn't check '{target.Label}' for changes: {ex.Message}");
			}
		}

		return CommandResult.Success(null, JsonSerializer.Serialize(new { state, label = target.Label }));
	}

	/// <summary>
	/// Tears down a slot's live backend, leaving it dormant (a faded chip): if it was active, binds the primary
	/// first, then disposes its <see cref="HostSession"/> while keeping the slot so the worktree stays surfaced.
	/// </summary>
	private async Task UnloadSlotAsync(SessionSlot slot) {
		if (slot.Session is not { } session) {
			return;
		}

		if (ReferenceEquals(_sessions?.ActiveSlot, slot) && PrimarySlot() is { } primary) {
			SwitchToSlot(primary);
		}

		slot.Session = null;
		await session.DisposeAsync().ConfigureAwait(false);
		PushSessionList();
	}

	private SessionSlot? PrimarySlot() => _sessions?.Slots.FirstOrDefault(s => s.IsPrimary);

	/// <summary>
	/// True when <paramref name="worktreePath"/> is still an inspectable git worktree (directory exists + carries
	/// its <c>.git</c> linkage). A failed delete can leave a folder with no <c>.git</c>; the delete path treats
	/// such a leftover as removable and skips the change inspection git can no longer answer.
	/// </summary>
	private static bool IsLiveWorktree(string worktreePath) =>
		Directory.Exists(worktreePath) && Path.Exists(Path.Combine(worktreePath, ".git"));

	private async Task<CommandResult> CreateWorktreeSessionAsync(string? requestedBranch, string? baseSpec, string? prompt, CancellationToken ct) {
		if (_worktrees is null) {
			return CommandResult.Failure("This workspace isn't a git repository, so worktree-backed sessions aren't available.");
		}

		string branch;
		if (string.IsNullOrWhiteSpace(requestedBranch)) {
			branch = await DeriveUniqueBranchNameAsync(prompt, ct).ConfigureAwait(false);
		} else {
			branch = requestedBranch.Trim();
			// The branch name is web-supplied; reject a malformed/option-shaped name before it reaches git.
			if (!GitService.IsValidBranchName(branch)) {
				return CommandResult.Failure($"'{branch}' isn't a valid branch name.");
			}
		}

		string baseRef;
		try {
			baseRef = await ResolveBaseRefAsync(baseSpec, ct).ConfigureAwait(false);
		} catch (GitException ex) {
			return CommandResult.Failure($"Couldn't resolve the base ref: {ex.Message}");
		}

		WorktreeRecord record;
		try {
			record = await _worktrees.CreateAsync(branch, baseRef, ct).ConfigureAwait(false);
		} catch (Exception ex) when (ex is InvalidOperationException or GitException) {
			return CommandResult.Failure($"Couldn't create the worktree: {ex.Message}");
		}

		// Run the user's setup command (e.g. `pnpm install`) in the background so the session opens now; it
		// toasts "setting up… → ready/failed" as it goes.
		StartWorktreeSetup(record.Path);
		return await BuildAndSwitchSlotAsync(branch, record, prompt, $"Created session on branch '{branch}' at {record.Path}.").ConfigureAwait(false);
	}

	/// <summary>
	/// Creates a session by checking out an existing branch into a new worktree. If Weavie already has a session
	/// for that branch — or it's the primary checkout's own branch — switches to that instead of duplicating.
	/// </summary>
	private async Task<CommandResult> AttachExistingSessionAsync(string? requestedBranch, string? prompt, CancellationToken ct) {
		if (_worktrees is not { } worktrees) {
			return CommandResult.Failure("This workspace isn't a git repository, so worktree-backed sessions aren't available.");
		}

		if (string.IsNullOrWhiteSpace(requestedBranch)) {
			return CommandResult.Failure("Pick an existing branch to check out.");
		}

		string branch = requestedBranch.Trim();
		if (!GitService.IsValidBranchName(branch)) {
			return CommandResult.Failure($"'{branch}' isn't a valid branch name.");
		}

		// Already a live/dormant Weavie session for this branch (slot ids are the branch name)? Switch to it.
		if (_sessions?.Find(branch) is { } existingSlot) {
			return await SwitchToExistingAsync(existingSlot, branch).ConfigureAwait(false);
		}

		// The branch checked out in the primary repo can't be attached to a second worktree (git refuses), so
		// the right move is to focus the primary session.
		try {
			string? primaryBranch = await new GitService().GetCurrentBranchAsync(WorkspaceRoot, ct).ConfigureAwait(false);
			if (string.Equals(primaryBranch, branch, StringComparison.Ordinal) && PrimarySlot() is { } primarySlot) {
				return await SwitchToExistingAsync(primarySlot, branch).ConfigureAwait(false);
			}
		} catch (GitException ex) {
			return CommandResult.Failure($"Couldn't read the current branch: {ex.Message}");
		}

		// Only run setup on a freshly-created worktree; a branch Weavie already tracks reuses its existing one.
		bool freshWorktree = worktrees.Registry.FindByBranch(branch) is null;
		WorktreeRecord record;
		try {
			record = await worktrees.AttachAsync(branch, ct).ConfigureAwait(false);
		} catch (Exception ex) when (ex is InvalidOperationException or GitException) {
			return CommandResult.Failure($"Couldn't check out '{branch}': {ex.Message}");
		}

		if (freshWorktree) {
			StartWorktreeSetup(record.Path);
		}

		// Seed the first prompt only on this fresh-checkout path; switching to an existing session (above) must
		// never re-seed it. The Open-PR flow uses this to brief Claude on the PR it just checked out.
		return await BuildAndSwitchSlotAsync(branch, record, prompt, $"Checked out '{branch}' at {record.Path}.").ConfigureAwait(false);
	}

	/// <summary>
	/// Builds a <see cref="SessionSlot"/> for a worktree <paramref name="record"/>, adds it to the rail, and
	/// switches to it on the UI thread (optionally seeding a first prompt).
	/// </summary>
	private Task<CommandResult> BuildAndSwitchSlotAsync(string branch, WorktreeRecord record, string? prompt, string successMessage) {
		var result = new TaskCompletionSource<CommandResult>();
		_ui.Post(() => {
			try {
				var slot = new SessionSlot {
					Id = branch,
					Label = branch,
					WorktreePath = record.Path,
					IsPrimary = false,
					Session = CreateSession(record.Path),
				};
				_sessions?.Add(slot, activate: false);
				SwitchToSlot(slot);
				if (!string.IsNullOrWhiteSpace(prompt)) {
					SeedFirstPrompt(slot.Session!, prompt);
				}

				result.SetResult(CommandResult.Success(successMessage));
			} catch (Exception ex) {
				result.SetException(ex);
			}
		});
		return result.Task;
	}

	/// <summary>Switches to an already-existing session slot (on the UI thread) and reports it.</summary>
	private Task<CommandResult> SwitchToExistingAsync(SessionSlot slot, string branch) {
		var result = new TaskCompletionSource<CommandResult>();
		_ui.Post(() => {
			try {
				SwitchToSlot(slot);
				result.SetResult(CommandResult.Success($"Switched to the existing session for '{branch}'."));
			} catch (Exception ex) {
				result.SetException(ex);
			}
		});
		return result.Task;
	}

	private async Task<string> ResolveBaseRefAsync(string? baseSpec, CancellationToken ct) {
		var git = new GitService();
		if (string.Equals(baseSpec, "main", StringComparison.OrdinalIgnoreCase)) {
			return await git.ResolveDefaultBranchAsync(WorkspaceRoot, ct).ConfigureAwait(false)
				?? await git.GetHeadCommitAsync(WorkspaceRoot, ct).ConfigureAwait(false);
		}

		// Default ("current"): branch off the active session's worktree HEAD.
		string cwd = _session?.WorkspaceRoot ?? WorkspaceRoot;
		return await git.GetHeadCommitAsync(cwd, ct).ConfigureAwait(false);
	}

	/// <summary>
	/// Derives a unique branch name for an auto-named session: a slug from the first prompt (or "session"),
	/// suffixed -2/-3/… until it collides with no existing slot label or worktree branch.
	/// </summary>
	private async Task<string> DeriveUniqueBranchNameAsync(string? prompt, CancellationToken ct) {
		var taken = new HashSet<string>(StringComparer.Ordinal);
		if (_sessions is not null) {
			foreach (var slot in _sessions.Slots) {
				taken.Add(slot.Label);
			}
		}

		if (_worktrees is not null) {
			try {
				foreach (var status in await _worktrees.ListAsync(ct).ConfigureAwait(false)) {
					if (status.Branch is { } existing) {
						taken.Add(existing);
					}
				}
			} catch (GitException) {
				// Best-effort: fall back to slot-label uniqueness; CreateAsync still rejects a true collision.
			}
		}

		string slug = "session";
		if (!string.IsNullOrWhiteSpace(prompt)) {
			char[] chars = [.. prompt.Trim().ToLowerInvariant().Take(40).Select(c => char.IsLetterOrDigit(c) ? c : '-')];
			slug = new string(chars).Trim('-');
			if (slug.Length == 0) {
				slug = "session";
			}
		}

		string candidate = slug;
		int n = 2;
		while (taken.Contains(candidate)) {
			candidate = $"{slug}-{n}";
			n++;
		}

		return candidate;
	}

	// Seed claude's first prompt by typing it into the PTY once the TUI is up. Best-effort; not load-bearing.
	private static void SeedFirstPrompt(HostSession session, string prompt) {
		_ = Task.Run(async () => {
			await Task.Delay(2500).ConfigureAwait(false);
			byte[] text = System.Text.Encoding.UTF8.GetBytes(prompt);
			session.Claude.Write(text);
			await Task.Delay(150).ConfigureAwait(false);
			session.Claude.Write([(byte)'\r']);
		});
	}
}
