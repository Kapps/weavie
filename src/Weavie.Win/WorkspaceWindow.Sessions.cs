using System.Text.Json;
using Weavie.Core;
using Weavie.Core.Commands;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Git;
using Weavie.Core.Sessions;
using Weavie.Core.Theming;
using Weavie.Core.Worktrees;
using Weavie.Win.Hosting;

namespace Weavie.Win;

// Multi-session + worktree wiring for the workspace window. The rail surfaces one SessionSlot per worktree
// (plus the primary checkout); a slot is either LOADED (a live HostSession — terminals/IDE-MCP/LSP) or
// UNLOADED (dormant, shown faded), so a worktree left on disk from a past run is always visible rather than
// leaking. Clicking a dormant chip loads it on demand; closing a session unloads it (the chip stays). The
// active session's backend drives the page (pushes gated on IsActiveSession); switching swaps terminals +
// repaints, rebinds the editor to the slot's worktree tabs, and re-pushes status + the rail. LSP doesn't yet
// re-bind on switch. See docs/specs/multi-session-and-worktrees.md.
internal sealed partial class WorkspaceWindow : ISessionHost {
	/// <summary>
	/// Wires a session's command handlers + its change/status/diff push subscriptions. The push
	/// subscriptions are gated on <see cref="IsActiveSession"/> so only the active session updates the page;
	/// for a single session that gate is always true, so behavior is identical to before.
	/// </summary>
	private void WireSession(HostSession session) {
		session.Commands.WebInvoker = InvokeWebCommandAsync;
		session.Commands.RegisterHandler(CoreCommands.ReopenTerminal, (_, _) => {
			RunOnUi(() => session.Shell.Restart());
			return Task.FromResult(CommandResult.Success("Reopened the terminal."));
		});
		session.Commands.RegisterHandler(CoreCommands.ToggleWindow, (_, _) => {
			RunOnUi(() => WindowFocus.Toggle(this));
			return Task.FromResult(CommandResult.Success("Toggled the Weavie window."));
		});
		ThemeCommands.RegisterHandlers(session.Commands, _settings, _app.ThemeOverrides, PickVsixFileAsync);
		SessionCommands.RegisterHandlers(session.Commands, this);

		session.Changes.Changed += () => {
			if (IsActiveSession(session)) {
				PushChangesToWeb();
			}
		};
		session.Changes.FileChanged += path => {
			if (IsActiveSession(session)) {
				PushRefreshToWeb(path);
				PushTurnDiffToWeb(path);
			}
		};
		session.Changes.TurnBegan += () => {
			if (IsActiveSession(session)) {
				PushTurnReset();
			}
		};
		session.Status.Changed += status => {
			if (IsActiveSession(session)) {
				PostSessionStatus(status);
			}

			RunOnUi(PushSessionList);
		};
		session.Lsp.FileChanges += changes => {
			if (IsActiveSession(session)) {
				PushWatcherChangesToWeb(changes);
			}
		};
	}

	private bool IsActiveSession(HostSession session) => ReferenceEquals(_session, session);

	/// <summary>Builds the workspace's worktree manager, or returns <c>null</c> when the root is not a git repo.</summary>
	private async Task<WorktreeManager?> BuildWorktreeManagerAsync() {
		var git = new GitService();
		try {
			if (!await git.IsRepositoryAsync(_workspaceRoot).ConfigureAwait(true)) {
				return null;
			}
		} catch (GitException) {
			// git not installed / not on PATH: worktree-backed sessions aren't available, but the workspace
			// still opens normally. Never let a missing git break window init.
			return null;
		}

		var registry = new WorktreeRegistry(new LocalFileSystem(), WeaviePaths.WorkspaceWorktreesFile(Id));
		registry.Log += line => Console.WriteLine($"[worktrees] {line}");
		return new WorktreeManager(git, registry, _workspaceRoot, WeaviePaths.WorkspaceWorktreesDir(Id));
	}

	/// <summary>Creates and registers the primary (workspace-root) slot, already loaded with <see cref="_session"/>.</summary>
	private void AddPrimarySlot(string label) {
		_sessions?.Add(new SessionSlot {
			Id = _session!.Id,
			Label = label,
			WorktreePath = _workspaceRoot,
			IsPrimary = true,
			Session = _session,
			LastActiveUtc = DateTimeOffset.UtcNow,
		}, activate: true);
	}

	/// <summary>
	/// On open, reconcile the worktree registry against real git so a crash or an external removal can't leave
	/// a worktree unsurfaced, then add an UNLOADED (dormant) slot for every existing non-primary worktree so it
	/// appears on the rail (faded) instead of leaking invisibly. Orphans (managed but gone) are skipped.
	/// </summary>
	private async Task ReconcileWorktreesOnOpenAsync() {
		if (_worktrees is null || _sessions is null) {
			return;
		}

		try {
			var report = await _worktrees.ReconcileAsync().ConfigureAwait(true);
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
		}
	}

	/// <summary>Pushes the session list (id, label, active, loaded, status, identity) to the page's rail.</summary>
	private void PushSessionList() {
		if (_sessions is null) {
			return;
		}

		// Loaded sessions first (in creation order), dormant ones sorted to the bottom — a parked session
		// shouldn't sit between two live ones. OrderByDescending is a stable sort, so order within each group
		// is preserved; the primary is always loaded, so it stays at the top. The web renders this order as-is
		// and skips dormant chips when cycling.
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

	private async Task<string> ResolvePrimaryLabelAsync() {
		try {
			var git = new GitService();
			if (await git.IsRepositoryAsync(_workspaceRoot).ConfigureAwait(true)) {
				string? branch = await git.GetCurrentBranchAsync(_workspaceRoot).ConfigureAwait(true);
				if (!string.IsNullOrWhiteSpace(branch)) {
					return branch;
				}
			}
		} catch (GitException) {
			// No git → fall back to the folder name for the rail label.
		}

		return Path.GetFileName(_workspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
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
			_bridge, _settings, _layout, cwd, WeaviePaths.WorkspaceScratchDir(Id), _pageOrigin,
			Guid.NewGuid().ToString("n")[..8],
			_app.CommandRegistry, _app.Keybindings, _app.ThemeOverrides);
		WireSession(session);
		return session;
	}

	/// <summary>Brings up the backend for an unloaded (dormant) slot: builds + wires its HostSession. No-op if already loaded.</summary>
	private void LoadSlot(SessionSlot slot) {
		if (!slot.Loaded) {
			slot.Session = CreateSession(slot.WorktreePath);
		}
	}

	/// <summary>
	/// Loads a dormant slot's backend IN THE BACKGROUND — creates its <see cref="HostSession"/> and starts its
	/// terminals at the default size so its Claude actually runs and reports status — WITHOUT binding the page to
	/// it. The session stays muted (<see cref="TerminalController.OutputActive"/> false) until the user switches
	/// to it, at which point <see cref="TerminalController.OnReady"/> resizes the live child to the real pane.
	/// This is the rail's "Load session" (load, don't open). No-op if already loaded.
	/// </summary>
	private void LoadSlotInBackground(SessionSlot slot) {
		if (slot.Loaded) {
			return;
		}

		LoadSlot(slot);
		var session = slot.Session!;
		// Not the visible session: don't stream its PTY output to the page's xterm (bound to the active session).
		session.Claude.OutputActive = false;
		session.Shell.OutputActive = false;
		// Start the backends now so Claude runs in the background; the page never sent term-ready for this slot,
		// so without this the child would never spawn.
		session.Claude.EnsureStarted();
		session.Shell.EnsureStarted();
		slot.LastActiveUtc = DateTimeOffset.UtcNow;
		PushSessionList();
	}

	/// <summary>
	/// Binds the page to <paramref name="slot"/>, loading its backend first if it was dormant: mutes the
	/// previous session's terminals (they keep running in the background), unmutes the new one's, and resets
	/// the page's xterms so they re-emit term-ready and the new session's terminals start/repaint. Rebinds the
	/// editor to this slot's worktree tabs, then re-pushes status + the rail. Records the slot's last-active
	/// time (the signal a future idle-unload sweep reads).
	/// </summary>
	private void SwitchToSlot(SessionSlot slot) {
		LoadSlot(slot);
		var session = slot.Session!;

		var previous = _session;
		if (previous is not null && !ReferenceEquals(previous, session)) {
			previous.Claude.OutputActive = false;
			previous.Shell.OutputActive = false;
		}

		_session = session;
		_sessions?.SetActive(slot);
		slot.LastActiveUtc = DateTimeOffset.UtcNow;
		session.Claude.OutputActive = true;
		session.Shell.OutputActive = true;

		// Clear the page's xterms; the page re-emits term-ready, which routes to the now-active session's
		// terminals (OnReady spawns a freshly-created session's PTYs; an already-live TUI is repainted).
		_bridge.PostToWeb("{\"type\":\"term-reset\",\"session\":\"claude\"}");
		_bridge.PostToWeb("{\"type\":\"term-reset\",\"session\":\"shell\"}");
		// Rebind the editor to this session's worktree: push its open tabs so the page closes the previous
		// session's working copies and reopens this one's. fs-read/write + active-editor already route to the
		// active session, so edits land in the right worktree the moment _session is swapped above.
		PushSessionEditorToWeb(session);
		// Re-root the omnibar quick-open + file browser to this session's worktree. Without this they keep
		// listing the previous session's files, and opening one fails — the new session's file provider refuses
		// to read a path outside its worktree (it surfaces as "nonexistent file").
		PushFileIndexToWeb();
		PostSessionStatus(session.Status.Status);
		PushSessionList();
	}

	/// <summary>
	/// Pushes a session's open editor tabs (a <c>set-editor-session</c>) so the page rebinds the editor to
	/// that session's worktree files. Built from the session's in-memory <see cref="HostSession.EditorSession"/>
	/// against its own filesystem (so missing files are dropped). The primary's tabs are kept in sync with the
	/// persisted store via <c>editor-session-changed</c>; secondary sessions accumulate theirs in memory.
	/// </summary>
	private void PushSessionEditorToWeb(HostSession session) =>
		_bridge.PostToWeb(EditorSessionStore.BuildRestoreJson(
			session.EditorSession, session.FileSystem, line => Console.WriteLine($"[weavie] {line}")));

	/// <inheritdoc/>
	public Task<CommandResult> NewSessionAsync(NewSessionRequest request, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(request);
		return CreateWorktreeSessionAsync(request.Branch, request.Base, request.Prompt, ct);
	}

	/// <inheritdoc/>
	public Task<CommandResult> ForkSessionAsync(ForkSessionRequest request, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(request);
		// Fork = a new worktree off the current session's HEAD, seeded with the handoff brief (PTY-injected).
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
		RunOnUi(() => {
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
		RunOnUi(async () => {
			try {
				await UnloadSlotAsync(target).ConfigureAwait(true);
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
		RunOnUi(async () => {
			try {
				// Check for uncommitted work BEFORE tearing anything down, so a blocked delete leaves the session
				// exactly as it was rather than unloading it as a side effect. (RemoveAsync re-checks under force:false.)
				if (!force && await new GitService().HasUncommittedChangesAsync(worktreePath, ct).ConfigureAwait(true)) {
					result.SetResult(CommandResult.Failure(
						$"Session '{label}' has uncommitted changes; deleting would discard them. Re-run with force to delete anyway."));
					return;
				}

				// Tear the live backend down first so no process keeps a handle on the worktree dir (Windows file
				// locks), then remove the worktree — keeping the branch — and drop the chip from the rail.
				if (target.Loaded) {
					await UnloadSlotAsync(target).ConfigureAwait(true);
				}

				await worktrees.RemoveAsync(worktreePath, deleteBranch: false, force, ct).ConfigureAwait(true);
				_sessions?.Remove(target);
				PushSessionList();
				result.SetResult(CommandResult.Success($"Deleted session '{label}': its worktree was removed and the branch kept."));
			} catch (WorktreeDirtyException) {
				result.SetResult(CommandResult.Failure(
					$"Session '{label}' has uncommitted changes; deleting would discard them. Re-run with force to delete anyway."));
			} catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException) {
				result.SetResult(CommandResult.Failure($"Couldn't delete session '{label}': {ex.Message}"));
			}
		});
		return result.Task;
	}

	/// <summary>
	/// Tears down a slot's live backend, leaving it dormant (a faded chip): if it was active, binds the primary
	/// first, then disposes its <see cref="HostSession"/> while keeping the slot so the worktree stays
	/// surfaced. The path a future idle-unload sweep reuses; deleting the worktree itself is separate.
	/// </summary>
	private async Task UnloadSlotAsync(SessionSlot slot) {
		if (slot.Session is not { } session) {
			return;
		}

		if (ReferenceEquals(_sessions?.ActiveSlot, slot) && PrimarySlot() is { } primary) {
			SwitchToSlot(primary);
		}

		slot.Session = null;
		await session.DisposeAsync().ConfigureAwait(true);
		PushSessionList();
	}

	private SessionSlot? PrimarySlot() => _sessions?.Slots.FirstOrDefault(s => s.IsPrimary);

	private async Task<CommandResult> CreateWorktreeSessionAsync(string? requestedBranch, string? baseSpec, string? prompt, CancellationToken ct) {
		if (_worktrees is null) {
			return CommandResult.Failure("This workspace isn't a git repository, so worktree-backed sessions aren't available.");
		}

		string branch = string.IsNullOrWhiteSpace(requestedBranch)
			? await DeriveUniqueBranchNameAsync(prompt, ct).ConfigureAwait(true)
			: requestedBranch.Trim();
		string baseRef;
		try {
			baseRef = await ResolveBaseRefAsync(baseSpec, ct).ConfigureAwait(true);
		} catch (GitException ex) {
			return CommandResult.Failure($"Couldn't resolve the base ref: {ex.Message}");
		}

		WorktreeRecord record;
		try {
			record = await _worktrees.CreateAsync(branch, baseRef, ct).ConfigureAwait(true);
		} catch (Exception ex) when (ex is InvalidOperationException or GitException) {
			return CommandResult.Failure($"Couldn't create the worktree: {ex.Message}");
		}

		var result = new TaskCompletionSource<CommandResult>();
		RunOnUi(() => {
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

				result.SetResult(CommandResult.Success($"Created session on branch '{branch}' at {record.Path}."));
			} catch (Exception ex) {
				result.SetException(ex);
			}
		});
		return await result.Task.ConfigureAwait(false);
	}

	private async Task<string> ResolveBaseRefAsync(string? baseSpec, CancellationToken ct) {
		var git = new GitService();
		if (string.Equals(baseSpec, "main", StringComparison.OrdinalIgnoreCase)) {
			return await git.ResolveDefaultBranchAsync(_workspaceRoot, ct).ConfigureAwait(true)
				?? await git.GetHeadCommitAsync(_workspaceRoot, ct).ConfigureAwait(true);
		}

		// Default ("current"): branch off the active session's worktree HEAD.
		string cwd = _session?.WorkspaceRoot ?? _workspaceRoot;
		return await git.GetHeadCommitAsync(cwd, ct).ConfigureAwait(true);
	}

	/// <summary>
	/// Derives a unique branch name for an auto-named session: a slug from the first prompt (or "session"),
	/// suffixed -2/-3/… until it collides with neither an existing slot label nor an existing worktree branch.
	/// Checking the live worktrees avoids the "branch already exists" failure when a prior session's worktree
	/// was left on disk (the leak the user can't otherwise see).
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
				foreach (var status in await _worktrees.ListAsync(ct).ConfigureAwait(true)) {
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

	// Experimental: seed the new session's claude with a first prompt by typing it into the PTY once the TUI
	// is up. Best-effort (a short delay for the TUI to start); not load-bearing. See the spec's fork section.
	private void SeedFirstPrompt(HostSession session, string prompt) {
		_ = Task.Run(async () => {
			await Task.Delay(2500).ConfigureAwait(false);
			byte[] text = System.Text.Encoding.UTF8.GetBytes(prompt);
			session.Claude.Write(text);
			await Task.Delay(150).ConfigureAwait(false);
			session.Claude.Write([(byte)'\r']);
		});
	}

	private void RunOnUi(Action action) {
		if (InvokeRequired) {
			BeginInvoke(action);
		} else {
			action();
		}
	}
}
