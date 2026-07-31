using System.Text.Json;
using Weavie.Core;
using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Git;
using Weavie.Core.Sessions;
using Weavie.Core.Theming;
using Weavie.Core.Workspaces;
using Weavie.Core.Worktrees;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

// HostCore's worktree/slot orchestration: one SessionSlot per worktree (plus primary), each loaded or dormant.
public sealed partial class HostCore {
	/// <summary>Wires behavior to the owning session bus. No callback observes client selection.</summary>
	private void WireSession(HostSession session) {
		session.EditorSessionChanged += state => {
			if (ReferenceEquals(session, _primarySession)) {
				_editorSession.Update(state);
			}
		};
		session.Commands.WebInvoker = (id, args, ct) => InvokeWebCommandAsync(session, id, args, ct);
		session.Commands.RegisterHandler(CoreCommands.ReopenTerminal, (_, _) => {
			_ui.Post(() => session.Shell.Restart());
			return Task.FromResult(CommandResult.Success("Reopened the terminal."));
		});
		session.Commands.RegisterHandler(CoreCommands.RestartAgent, (_, _) => {
			_ui.Post(session.RestartAgent);
			return Task.FromResult(CommandResult.Success("Restarted the agent."));
		});
		// Restart-now for a pending update: the user's explicit choice to skip the drain gate (kills
		// running shell jobs); fails cleanly when no update is pending.
		session.Commands.RegisterHandler(CoreCommands.RestartForUpdate, (_, _) =>
			Task.FromResult(RestartNowForUpdate()));
		session.Commands.RegisterHandler(CoreCommands.ToggleWindow, (_, _) => {
			_ui.Post(_platform.ToggleWindow);
			return Task.FromResult(CommandResult.Success("Toggled the Weavie window."));
		});
		// Session-bound command handlers always act on their owner, even while another client session is selected.
		session.Commands.RegisterHandler(CoreCommands.SetupWorkspace, (_, _) => {
			_ui.Post(() => SeedWorkspaceSetup(session));
			return Task.FromResult(CommandResult.Success("Asked Claude to set up this workspace."));
		});
		session.Commands.RegisterHandler(
			CoreCommands.LearnFromCorrections,
			(_, _) => Task.FromResult(RunLearn(session)));
		// Connect Notion: open the token page in the browser and ask the page to show the token input (the user
		// pastes it there; set-source-token validates + saves). Synchronous — the work happens on the page.
		session.Commands.RegisterHandler(CoreCommands.ConnectNotion, (_, _) => {
			PromptConnectNotion(session);
			return Task.FromResult(CommandResult.Success("Opening your browser to connect Notion…"));
		});
		// View Logs: snapshot the captured console output into a read-only tab + return the recent tail (see
		// HostCore.Logs.cs). The tab opens on the invoking session's bus.
		session.Commands.RegisterHandler(CoreCommands.ViewLogs, (_, _) => Task.FromResult(ShowLogs(session)));
		RegisterTestRunHandlers(session);
		ThemeCommands.RegisterHandlers(session.Commands, _settings, _themeOverrides, VsixPicker);
		FontCommands.RegisterHandlers(session.Commands, _settings);
		SessionCommands.RegisterHandlers(session.Commands, new BoundSessionHost(this, session));
		WireCoreSessionMessages(session);

		session.Changes.Changed += () => PostForSession(session, () => PushTurnChangesToWeb(session));
		session.Changes.FileChanged += path => PostForSession(session, () => {
			PushRefreshToWeb(session, path);
			PushTurnDiffToWeb(session, path);
		});
		session.Changes.FileDeleted += path =>
			PostForSession(session, () => PushDeletionToWeb(session, path));
		session.Changes.AcceptedCommitted += paths => PostForSession(session, () => {
			PushTurnChangesToWeb(session);
			foreach (string path in paths) {
				PushTurnDiffToWeb(session, path);
			}

			PushReviewHistoryToWeb(session);
		});
		WireAttention(session);
		session.Status.Changed += status => PostForSession(session, () => {
			PostSessionStatus(session, status);
			PushGitStatus(session);
			if (status is SessionStatus.Idle or SessionStatus.Waiting) {
				PushPullRequestStatus(session);
			}

			if (Draining) {
				EvaluateDrain();
			}

			PushSessionList();
		});
		session.FileChanges += changes =>
			PostForSession(session, () => PushWatcherChangesToWeb(session, changes));
	}

	private void PostForSession(HostSession session, Action action) {
		_ = session.Background.Run(ct => RunOnUiAsync(() => {
			if (!ct.IsCancellationRequested) {
				action();
			}

			return Task.CompletedTask;
		}));
	}

	/// <summary>Test seam for one exact logical slot.</summary>
	internal HostSession? SessionForTest(string slot) =>
		_sessions?.Find(slot)?.Session
		?? (slot == "primary" ? _primarySession : null);

	/// <summary>Every loaded session's live backend, in catalog order.</summary>
	private List<HostSession> LoadedSessions() {
		var list = new List<HostSession>();
		if (_sessions is not null) {
			foreach (var slot in _sessions.Slots) {
				if (slot.Session is { } session) {
					list.Add(session);
				}
			}
		} else if (_primarySession is not null) {
			list.Add(_primarySession);
		}

		return list;
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

		// Runs worktree.setupCommand/teardownCommand around create/discard. The command strings are read live
		// from settings, resolved against this workspace so its out-of-repo overlay is consulted (like test.profile);
		// progress + results surface as toasts (and full output to the console).
		var provisioner = new ShellWorktreeProvisioner(
			() => _settings.GetString("worktree.setupCommand", WorkspaceRoot),
			() => _settings.GetString("worktree.teardownCommand", WorkspaceRoot));
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
					"warn", $"Worktree setup for '{WorktreeLabel(worktreePath)}' couldn't run: {ex.Message}"));
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
				Notify("warn", $"Worktree {phase} command failed (exit {result.ExitCode}) — see console.");
			}
		});
	}

	private static string WorktreeLabel(string path) =>
		Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

	/// <summary>Creates and registers the primary (workspace-root) slot, already loaded with the primary session.</summary>
	private void AddPrimarySlot(string label) {
		_primarySession!.DisplayLabel = label;
		var slot = new SessionSlot {
			Id = _primarySession.SlotId,
			Label = label,
			WorktreePath = WorkspaceRoot,
			IsPrimary = true,
			AgentProviderId = "claude",
			Session = _primarySession,
		};
		_sessions?.Add(slot);
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
				string agentProviderId = ProviderFor(status, label);
				BackfillWorktreeProvider(status, agentProviderId);
				if (_sessions.Find(label) is not null) {
					continue; // already surfaced
				}

				_sessions.Add(new SessionSlot {
					Id = label,
					Label = label,
					WorktreePath = status.Path,
					IsPrimary = false,
					AgentProviderId = agentProviderId,
					Session = null,
				});
			}

			PushSessionList();
		} catch (GitException ex) {
			Console.WriteLine($"[weavie] worktree reconcile failed: {ex.Message}");
			Notify("warn", "Couldn't list existing worktrees — some sessions may not appear on the rail.");
		}
	}

	private string ProviderFor(WorktreeStatus status, string slotId) =>
		ProviderOrNull(status.AgentProviderId)
		?? PersistedProviderFor(slotId, status.Path)
		?? "claude";

	private string? PersistedProviderFor(string slotId, string worktreePath) {
		string? provider = _sessionStore.Items.FirstOrDefault(item =>
			string.Equals(item.Id.Value, slotId, StringComparison.Ordinal)
			|| PathsEqual(item.WorktreePath, worktreePath))?.AgentProviderId;
		return ProviderOrNull(provider);
	}

	private void BackfillWorktreeProvider(WorktreeStatus status, string agentProviderId) {
		if (!status.IsManaged || ProviderOrNull(status.AgentProviderId) is not null) {
			return;
		}

		if (_worktrees?.Registry.FindByPath(status.Path) is { } record) {
			_worktrees.Registry.Add(record with { AgentProviderId = agentProviderId });
		}
	}

	private static string? ProviderOrNull(string? provider) =>
		string.IsNullOrWhiteSpace(provider) ? null : provider.Trim();

	private static bool PathsEqual(string a, string b) =>
		string.Equals(
			Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
			Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
			OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

	private string ResolveNewSessionProvider(string? requestedProvider) {
		if (!string.IsNullOrWhiteSpace(requestedProvider)) {
			return requestedProvider.Trim();
		}

		return _settings.RequireString(AgentSettings.DefaultProvider);
	}

	/// <summary>Persists a chosen provider as the standing default, so the next new session preselects it. Any
	/// registered provider sticks — including one only installed on a remote backend, where the session actually
	/// runs; local availability is irrelevant to a preselection the prompt always lets the user change. Only an
	/// unregistered id is dropped, as garbage that would fail session creation.</summary>
	private void RememberDefaultProvider(string? requestedProvider) {
		string? provider = requestedProvider?.Trim();
		if (!string.IsNullOrEmpty(provider) && _agentProviders.FindInfo(provider) is not null) {
			_settings.Set(AgentSettings.DefaultProvider, JsonSerializer.SerializeToElement(provider));
		}
	}

	/// <summary>Pushes the authoritative session catalog. Loaded entries carry their exact live address.</summary>
	private void PushSessionList() =>
		_messages.Host.Feature("sessions").Publish("catalog", BuildSessionCatalog());

	private void ActivateSessionMessages(HostSession session) {
		SyncSession(session, session.Bus.BroadcastTarget);
		session.ActivateMessages();
	}

	private SessionCatalogEntry[] BuildSessionCatalog() =>
		_sessions?.Slots
			.OrderByDescending(slot => slot.Loaded)
			.Select(slot => {
				var info = _agentProviders.FindInfo(slot.AgentProviderId);
				bool structured = info?.Capabilities
					.HasFlag(AgentProviderCapabilities.StructuredPane) == true;
				return new SessionCatalogEntry(
					slot.Id,
					slot.Label,
					slot.Session?.Address,
					slot.Loaded,
					slot.IsPrimary,
					slot.AgentProviderId,
					info is null ? "unavailable" : structured ? "structured" : "terminal",
					structured ? 2 : 0,
					slot.Session is { } session ? StatusName(session.Status.Status) : "idle",
					SessionIdentity.Hue(slot.Label),
					SessionIdentity.Monogram(slot.Label));
			})
			.ToArray()
		?? [];

	private sealed record SessionCatalogEntry(
		string Id,
		string Label,
		SessionAddress? Address,
		bool Loaded,
		bool Primary,
		string ProviderId,
		string AgentSurface,
		int AgentInputProtocol,
		string Status,
		int Hue,
		string Monogram);

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

	private static void PostSessionStatus(HostSession session, SessionStatus status) =>
		PostSessionStatus(session.Bus.BroadcastTarget, status);

	private static void PostSessionStatus(MessageTarget target, SessionStatus status) =>
		target.Feature("status").Publish("changed", new { status = StatusName(status) });

	// Exhaustive on purpose: a silent default that maps an unhandled status to "idle" would render it
	// drain-killable — exactly the Waiting bug — so a new status must be wired here, not fall through.
	private static string StatusName(SessionStatus status) => status switch {
		SessionStatus.Starting => "starting",
		SessionStatus.Working => "working",
		SessionStatus.NeedsInput => "needsInput",
		SessionStatus.Idle => "idle",
		SessionStatus.Waiting => "waiting",
		SessionStatus.Error => "error",
		_ => throw new ArgumentOutOfRangeException(nameof(status), status, "unhandled session status"),
	};

	/// <summary>Builds + wires a new <see cref="HostSession"/> rooted at <paramref name="cwd"/> (the live backend for a slot).</summary>
	private HostSession CreateSession(string cwd, string agentProviderId, string slotId) {
		var provider = _agentProviders.RequireAvailable(agentProviderId);
		var address = new SessionAddress(slotId, Guid.NewGuid().ToString("n"));
		var endpoint = _messages.OpenSession(address);
		HostSession? session = null;
		try {
			session = new HostSession(
				endpoint,
				_settings,
				_layout,
				cwd,
				Path.Combine(WeaviePaths.WorkspaceScratchDir(Id), WorkspaceId.ForPath(cwd).Value),
				// Pasted images go in a per-session subdir (keyed by worktree, like the scrollback log) so unloading
				// one session's images never touches another's.
				Path.Combine(WeaviePaths.WorkspacePastedImagesDir(Id), WorkspaceId.ForPath(cwd).Value),
				// The structured agent pane's durable transcript (keyed by worktree, like the shell scrollback log)
				// so its output restores across reload/unload/restart. Terminal-backed providers ignore it.
				WeaviePaths.WorkspaceAgentPaneFile(Id, WorkspaceId.ForPath(cwd).Value),
				_commandRegistry,
				_keybindings,
				_themeOverrides,
				_corrections,
				_platform.PtyLauncher,
				provider,
				_runtime,
				() => _drainInputFrozen,
				_sessionStore.RecordShellSize);

			// Persist the shell scrollback (keyed by worktree path, stable across reloads) so a reattaching client
			// replays a coherent screen. Shell only — claude resumes its own conversation.
			session.Shell.ScrollbackLogPath =
				WeaviePaths.WorkspaceTerminalLogFile(Id, WorkspaceId.ForPath(cwd).Value, "shell");
			// Seed the shell's pre-spawn size from the last real terminal size so a background-restored child is born at
			// the width its reattaching xterm will use — else its raw scrollback replays 80×24-wrapped and stacks garbled.
			if (_sessionStore.ShellSize is { } shellSize) {
				session.Shell.Resize(shellSize.Cols, shellSize.Rows);
			}

			WireSession(session);
			_mediaRoutes.Register(
				session.Incarnation,
				[session.WorkspaceRoot, session.Scratch.Directory, session.PastedImages.Directory]);
			return session;
		} catch (Exception creationError) {
			try {
				if (session is null) {
					endpoint.DisposeAsync().AsTask().GetAwaiter().GetResult();
				} else {
					session.DisposeAsync().AsTask().GetAwaiter().GetResult();
				}
			} catch (Exception cleanupError) {
				throw new AggregateException("Session construction and cleanup both failed.", creationError, cleanupError);
			}

			throw;
		}
	}

	/// <summary>
	/// Brings up an unloaded slot's exact-addressed backend. Idempotent when the slot is already loaded.
	/// </summary>
	private void LoadSlot(SessionSlot slot) {
		if (!slot.Loaded) {
			slot.Session = CreateSession(slot.WorktreePath, slot.AgentProviderId, slot.Id);
			slot.Session.DisplayLabel = slot.Label;
		}
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

		try {
			LoadSlot(slot);
			var session = slot.Session!;
			PushSessionList();
			ActivateSessionMessages(session);
			PersistSessionState();
			// Start the backends now so Claude runs even before its pane mounts (else it spawns on terminal ready); the
			// resize nudge on first mount repaints the live TUI.
			session.EnsureAgentStarted();
			session.Shell.EnsureStarted();
		} catch (Exception error) {
			throw RollbackSessionLoad(slot, removeSlot: false, error: error);
		}
	}

	private Exception RollbackSessionLoad(
		SessionSlot slot,
		bool removeSlot,
		Exception error) {
		var failures = new List<Exception> { error };
		if (slot.Session is { } session) {
			try {
				session.DisposeAsync().AsTask().GetAwaiter().GetResult();
			} catch (Exception cleanupError) {
				failures.Add(cleanupError);
			}

			if (ReferenceEquals(slot.Session, session)) {
				slot.Session = null;
			}
			_mediaRoutes.Unregister(session.Incarnation);
		}

		if (removeSlot) {
			_sessions?.Remove(slot);
		}

		try {
			PushSessionList();
			PersistSessionState();
		} catch (Exception catalogError) {
			failures.Add(catalogError);
		}

		return failures.Count == 1
			? error
			: new AggregateException("Session load and rollback both failed.", failures);
	}

	private Task<CommandResult> NewSessionAsync(
		HostSession source,
		NewSessionRequest request,
		CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(request);
		string provider = ResolveNewSessionProvider(request.AgentProviderId);
		return RunSessionLifecycleAsync(
			() => request.AttachExisting
				? AttachExistingSessionAsync(request.Branch, request.Prompt, provider, ct)
				: CreateWorktreeSessionAsync(source, request.Branch, request.Base, request.Prompt, provider, ct),
			ct);
	}

	private Task<CommandResult> ForkSessionAsync(
		HostSession source,
		ForkSessionRequest request,
		CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(request);
		string providerId = SlotFor(source)?.AgentProviderId ?? ResolveNewSessionProvider(null);
		return RunSessionLifecycleAsync(
			() => CreateWorktreeSessionAsync(source, request.Branch, "source", request.Handoff, providerId, ct),
			ct);
	}

	private Task<CommandResult> LoadSessionAsync(string? sessionId, CancellationToken ct) =>
		RunSessionLifecycleAsync(() => LoadSessionCoreAsync(sessionId, ct), ct);

	private Task<CommandResult> LoadSessionCoreAsync(string? sessionId, CancellationToken ct) {
		ct.ThrowIfCancellationRequested();
		if (string.IsNullOrWhiteSpace(sessionId)) {
			return Task.FromResult(CommandResult.Failure("Load needs a session id."));
		}

		var target = _sessions?.Find(sessionId);
		if (target is null) {
			return Task.FromResult(CommandResult.Failure("No such session."));
		}

		if (target.Loaded) {
			return Task.FromResult(CommandResult.Success(
				"That session is already loaded.",
				SessionAddressJson(target)));
		}

		try {
			_agentProviders.RequireAvailable(target.AgentProviderId);
		} catch (InvalidOperationException ex) {
			return Task.FromResult(CommandResult.Failure(ex.Message));
		}

		var result = new TaskCompletionSource<CommandResult>();
		_ui.Post(() => {
			try {
				LoadSlotInBackground(target);
				result.SetResult(CommandResult.Success(
					$"Loaded session '{target.Label}' in the background.",
					SessionAddressJson(target)));
			} catch (Exception ex) {
				result.SetException(ex);
			}
		});
		return result.Task;
	}

	private async Task<CommandResult> UnloadSessionAsync(
		HostSession source,
		string? sessionId,
		CommandInvocationContext context,
		CancellationToken ct) =>
		await RunSessionLifecycleAsync(
			() => UnloadSessionCoreAsync(source, sessionId, context, ct),
			ct).ConfigureAwait(false);

	private async Task<CommandResult> UnloadSessionCoreAsync(
		HostSession source,
		string? sessionId,
		CommandInvocationContext context,
		CancellationToken ct) {
		ct.ThrowIfCancellationRequested();
		var target = string.IsNullOrWhiteSpace(sessionId) ? null : _sessions?.Find(sessionId);
		if (target is null) {
			return CommandResult.Failure("No such session.");
		}

		if (target.IsPrimary) {
			return CommandResult.Failure("The primary session can't be unloaded; close the window instead.");
		}

		if (!target.Loaded) {
			return CommandResult.Success("That session is already unloaded.");
		}

		if (await FlushSessionViewAsync(target.Session!, ct).ConfigureAwait(false) is { } flushFailure) {
			return flushFailure;
		}

		if (ReferenceEquals(target.Session, source)) {
			context.AfterReply(() => UnloadAfterReplyAsync(target));
			return CommandResult.Success("Unloading the session (its worktree will be kept).");
		}

		await RunOnUiAsync(() => UnloadSlotAsync(target)).ConfigureAwait(false);
		return CommandResult.Success("Unloaded the session (its worktree is kept; click the chip to reload).");
	}

	private async Task UnloadAfterReplyAsync(SessionSlot target) {
		try {
			await RunSessionLifecycleAsync(
				() => RunOnUiAsync(() => UnloadSlotAsync(target)),
				CancellationToken.None).ConfigureAwait(false);
		} catch (Exception ex) {
			Notify("error", $"Couldn't unload session '{target.Label}': {ex.Message}");
			throw;
		}
	}

	private Task<CommandResult> DeleteSessionAsync(
		HostSession source,
		string? sessionId,
		bool force,
		CommandInvocationContext context,
		CancellationToken ct) =>
		RunSessionLifecycleAsync(
			() => DeleteSessionCoreAsync(source, sessionId, force, context, ct),
			ct);

	private Task<CommandResult> DeleteSessionCoreAsync(
		HostSession source,
		string? sessionId,
		bool force,
		CommandInvocationContext context,
		CancellationToken ct) {
		var target = string.IsNullOrWhiteSpace(sessionId) ? null : _sessions?.Find(sessionId);
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

		return DeleteWorktreeSessionAsync(
			source,
			target,
			worktrees,
			worktreePath,
			label,
			force,
			context,
			ct);
	}

	private async Task<CommandResult> DeleteWorktreeSessionAsync(
		HostSession source,
		SessionSlot target,
		WorktreeManager worktrees,
		string worktreePath,
		string label,
		bool force,
		CommandInvocationContext context,
		CancellationToken ct) {
		try {
			if (target.Session is { } session
				&& await FlushSessionViewAsync(session, ct).ConfigureAwait(false) is { } flushFailure) {
				return flushFailure;
			}

			// Check for uncommitted work BEFORE tearing anything down, so a blocked delete leaves the session
			// untouched rather than unloading it as a side effect. Skip when the worktree is gone/half-removed
			// (no .git) — nothing left to lose, and git can't answer git status there. A read-only git probe,
			// so it needs no UI-thread marshaling.
			if (!force && IsLiveWorktree(worktreePath)
				&& await new GitService().HasUncommittedChangesAsync(worktreePath, ct).ConfigureAwait(false)) {
				return CommandResult.Failure(
					$"Session '{label}' has uncommitted changes; deleting would discard them. Re-run with force to delete anyway.");
			}
		} catch (GitException ex) {
			return CommandResult.Failure($"Couldn't delete session '{label}': {ex.Message}");
		}

		if (ReferenceEquals(target.Session, source)) {
			context.AfterReply(() => DeleteAfterReplyAsync(target, worktrees, worktreePath, label, force));
			return CommandResult.Success($"Deleting session '{label}' (the branch will be kept).");
		}

		return await DeleteAfterPreflightAsync(target, worktrees, worktreePath, label, force).ConfigureAwait(false);
	}

	private async Task DeleteAfterReplyAsync(
		SessionSlot target,
		WorktreeManager worktrees,
		string worktreePath,
		string label,
		bool force) {
		var result = await RunSessionLifecycleAsync(
			() => DeleteAfterPreflightAsync(target, worktrees, worktreePath, label, force),
			CancellationToken.None).ConfigureAwait(false);
		if (!result.Ok) {
			Notify("error", result.Error ?? $"Couldn't delete session '{label}'.");
		}
	}

	private async Task<CommandResult?> FlushSessionViewAsync(HostSession session, CancellationToken ct) {
		try {
			var result = await session.View.Feature("editor")
				.TryRequestAsync<EmptySessionMessage, EditorFlushResult>(
					"flush",
					new EmptySessionMessage(),
					ct)
				.ConfigureAwait(false);
			if (result is not null) {
				HandleEditorSessionChanged(session, result.Session);
			}

			return null;
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return CommandResult.Failure(
				$"Couldn't save the editor state for session '{session.DisplayLabel}': {ex.Message}");
		}
	}

	private async Task<CommandResult> DeleteAfterPreflightAsync(
		SessionSlot target,
		WorktreeManager worktrees,
		string worktreePath,
		string label,
		bool force) {
		try {
			if (!ReferenceEquals(_sessions?.Find(target.Id), target)) {
				return CommandResult.Success($"Session '{label}' is already deleted.");
			}

			// Tear the live backend down first so no process holds the worktree dir, then remove the worktree
			// (keeping the branch). The unload starts on the UI thread to mutate the slot and this method awaits
			// its teardown from off it. Past the dirty guard deletion is deliberately uncancellable: self-delete
			// tears down the endpoint that accepted the command, and git must not be interrupted mid-removal.
			if (target.Loaded) {
				await RunOnUiAsync(() => UnloadSlotAsync(target)).ConfigureAwait(false);
			}

			// Settle before removal: Windows can lag on releasing the unloaded children's handles, and external
			// scanners may briefly hold a lock. A short pause lets git's one-shot remove succeed instead of
			// partial-failing and orphaning the directory (git deletes its own record mid-failure, unrecoverable).
			await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
			await worktrees.RemoveAsync(worktreePath, deleteBranch: false, force, CancellationToken.None).ConfigureAwait(false);
			// Back on the UI thread for the slot-set mutation + rail push (the awaits above left it), so the
			// removal can't interleave with a concurrent switch reading the slot set.
			await RunOnUiAsync(() => {
				_sessions?.Remove(target);
				PushSessionList();
				PersistSessionState();
				return Task.CompletedTask;
			}).ConfigureAwait(false);
			return CommandResult.Success($"Deleted session '{label}': its worktree was removed and the branch kept.");
		} catch (WorktreeDirtyException) {
			return CommandResult.Failure(
				$"Session '{label}' has uncommitted changes; deleting would discard them. Re-run with force to delete anyway.");
		} catch (WorktreeOrphanException ex) {
			return CommandResult.Failure($"Couldn't delete session '{label}': {ex.Message}");
		} catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException) {
			return CommandResult.Failure($"Couldn't delete session '{label}': {ex.Message}");
		}
	}

	private async Task<T> RunSessionLifecycleAsync<T>(
		Func<Task<T>> action,
		CancellationToken ct) {
		await _sessionLifecycle.WaitAsync(ct).ConfigureAwait(false);
		try {
			return await action().ConfigureAwait(false);
		} finally {
			_sessionLifecycle.Release();
		}
	}

	private async Task RunSessionLifecycleAsync(
		Func<Task> action,
		CancellationToken ct) {
		await _sessionLifecycle.WaitAsync(ct).ConfigureAwait(false);
		try {
			await action().ConfigureAwait(false);
		} finally {
			_sessionLifecycle.Release();
		}
	}

	/// <summary>
	/// Starts <paramref name="work"/> on the UI thread and returns its completion, so a caller already off the
	/// dispatcher can run host-catalog work (such as a slot detach) in order, then await
	/// its async tail (e.g. a backend teardown) from off it.
	/// </summary>
	private Task RunOnUiAsync(Func<Task> work) {
		var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_ui.Post(async () => {
			try {
				await work().ConfigureAwait(false);
				completion.SetResult();
			} catch (Exception ex) {
				completion.SetException(ex);
			}
		});
		return completion.Task;
	}

	private async Task<CommandResult> ClassifyDeleteAsync(string? sessionId, CancellationToken ct) {
		var target = string.IsNullOrWhiteSpace(sessionId) ? null : _sessions?.Find(sessionId);
		if (target is null) {
			return CommandResult.Failure("No such session.");
		}

		if (target.IsPrimary) {
			return CommandResult.Failure("The primary session can't be deleted; close the window instead.");
		}

		// A gone/half-removed worktree (no .git) can't be inspected and has nothing left to lose — classify clean.
		string state = "clean";
		IReadOnlyList<string> tracked = [];
		IReadOnlyList<string> untracked = [];
		if (IsLiveWorktree(target.WorktreePath)) {
			try {
				var status = await new GitService().GetChangeStateAsync(target.WorktreePath, ct).ConfigureAwait(false);
				state = status.State switch {
					WorktreeChangeState.UntrackedOnly => "untracked",
					WorktreeChangeState.Modified => "modified",
					_ => "clean",
				};
				tracked = status.TrackedFiles;
				untracked = status.UntrackedFiles;
			} catch (GitException ex) {
				return CommandResult.Failure($"Couldn't check '{target.Label}' for changes: {ex.Message}");
			}
		}

		// Name the first few changes the delete would discard; the dialog renders "…and N more" from the total.
		const int previewLimit = 5;
		string[] changed = [.. tracked.Concat(untracked).Order(StringComparer.Ordinal)];
		return CommandResult.Success(null, JsonSerializer.Serialize(new {
			state,
			label = target.Label,
			changedFiles = changed.Take(previewLimit).ToArray(),
			changedCount = changed.Length,
		}));
	}

	/// <summary>Tears down a slot's live backend, leaving its worktree as a dormant catalog entry.</summary>
	private async Task UnloadSlotAsync(SessionSlot slot) {
		if (slot.Session is not { } session) {
			return;
		}

		await session.DisposeAsync().ConfigureAwait(false);
		await RunOnUiAsync(() => {
			if (ReferenceEquals(slot.Session, session)) {
				slot.Session = null;
				_mediaRoutes.Unregister(session.Incarnation);
				PushSessionList();
				PersistSessionState();
			}

			return Task.CompletedTask;
		}).ConfigureAwait(false);
	}

	private SessionSlot? PrimarySlot() => _sessions?.Slots.FirstOrDefault(s => s.IsPrimary);

	/// <summary>The slot whose live backend is <paramref name="session"/>, or null (unloaded, or pre-rail during startup).</summary>
	private SessionSlot? SlotFor(HostSession session) =>
		_sessions?.Slots.FirstOrDefault(slot => ReferenceEquals(slot.Session, session));

	/// <summary>
	/// True when <paramref name="worktreePath"/> is still an inspectable git worktree (directory exists + carries
	/// its <c>.git</c> linkage). A failed delete can leave a folder with no <c>.git</c>; the delete path treats
	/// such a leftover as removable and skips the change inspection git can no longer answer.
	/// </summary>
	private static bool IsLiveWorktree(string worktreePath) =>
		Directory.Exists(worktreePath) && Path.Exists(Path.Combine(worktreePath, ".git"));

	private async Task<CommandResult> CreateWorktreeSessionAsync(
		HostSession source,
		string? requestedBranch,
		string? baseSpec,
		string? prompt,
		string agentProviderId,
		CancellationToken ct) {
		try {
			_agentProviders.RequireAvailable(agentProviderId);
		} catch (InvalidOperationException ex) {
			return CommandResult.Failure(ex.Message);
		}

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
			baseRef = await ResolveBaseRefAsync(source, baseSpec, ct).ConfigureAwait(false);
		} catch (Exception ex) when (ex is GitException or InvalidOperationException) {
			return CommandResult.Failure($"Couldn't resolve the base ref: {ex.Message}");
		}

		WorktreeRecord record;
		try {
			record = await _worktrees.CreateAsync(branch, baseRef, agentProviderId, ct).ConfigureAwait(false);
		} catch (Exception ex) when (ex is InvalidOperationException or GitException) {
			return CommandResult.Failure($"Couldn't create the worktree: {ex.Message}");
		}

		// Run the user's setup command (e.g. `pnpm install`) in the background so the session opens now; it
		// toasts "setting up… → ready/failed" as it goes.
		StartWorktreeSetup(record.Path);
		return await BuildSlotAsync(
			branch,
			record,
			prompt,
			agentProviderId,
			$"Created session on branch '{branch}' at {record.Path}.").ConfigureAwait(false);
	}

	/// <summary>
	/// Creates a session by checking out an existing branch into a new worktree. If Weavie already has a session
	/// for that branch — or it's the primary checkout's own branch — switches to that instead of duplicating.
	/// </summary>
	private async Task<CommandResult> AttachExistingSessionAsync(
		string? requestedBranch,
		string? prompt,
		string agentProviderId,
		CancellationToken ct) {
		try {
			_agentProviders.RequireAvailable(agentProviderId);
		} catch (InvalidOperationException ex) {
			return CommandResult.Failure(ex.Message);
		}

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
			return await LoadExistingAsync(existingSlot, branch).ConfigureAwait(false);
		}

		// The branch checked out in the primary repo can't be attached to a second worktree (git refuses), so
		// the right move is to focus the primary session.
		try {
			string? primaryBranch = await new GitService().GetCurrentBranchAsync(WorkspaceRoot, ct).ConfigureAwait(false);
			if (string.Equals(primaryBranch, branch, StringComparison.Ordinal) && PrimarySlot() is { } primarySlot) {
				return await LoadExistingAsync(primarySlot, branch).ConfigureAwait(false);
			}
		} catch (GitException ex) {
			return CommandResult.Failure($"Couldn't read the current branch: {ex.Message}");
		}

		// Only run setup on a freshly-created worktree; a branch Weavie already tracks reuses its existing one.
		bool freshWorktree = worktrees.Registry.FindByBranch(branch) is null;
		WorktreeRecord record;
		try {
			record = await worktrees.AttachAsync(branch, agentProviderId, ct).ConfigureAwait(false);
		} catch (Exception ex) when (ex is InvalidOperationException or GitException) {
			return CommandResult.Failure($"Couldn't check out '{branch}': {ex.Message}");
		}

		string slotProviderId = ProviderOrNull(record.AgentProviderId) ?? agentProviderId;
		if (!freshWorktree && ProviderOrNull(record.AgentProviderId) is null) {
			record = record with { AgentProviderId = slotProviderId };
			worktrees.Registry.Add(record);
		}

		if (freshWorktree) {
			StartWorktreeSetup(record.Path);
		}

		// Seed the first prompt only on this fresh-checkout path; switching to an existing session (above) must
		// never re-seed it. The Open-PR flow uses this to brief Claude on the PR it just checked out.
		return await BuildSlotAsync(
			branch,
			record,
			prompt,
			slotProviderId,
			$"Checked out '{branch}' at {record.Path}.").ConfigureAwait(false);
	}

	/// <summary>
	/// Builds a <see cref="SessionSlot"/> for a worktree <paramref name="record"/>, adds it to the rail, and
	/// returns its exact address so the calling page can select it (optionally seeding a first prompt).
	/// </summary>
	private Task<CommandResult> BuildSlotAsync(
		string branch,
		WorktreeRecord record,
		string? prompt,
		string agentProviderId,
		string successMessage) {
		var sessions = _sessions
			?? throw new InvalidOperationException("The session catalog is not initialized.");
		var result = new TaskCompletionSource<CommandResult>();
		_ui.Post(() => {
			SessionSlot? slot = null;
			try {
				slot = new SessionSlot {
					Id = branch,
					Label = branch,
					WorktreePath = record.Path,
					IsPrimary = false,
					AgentProviderId = agentProviderId,
					Session = CreateSession(record.Path, agentProviderId, branch),
				};
				sessions.Add(slot);
				PushSessionList();
				ActivateSessionMessages(slot.Session);
				PersistSessionState();
				if (!string.IsNullOrWhiteSpace(prompt)) {
					SeedFirstPrompt(slot.Session!, prompt);
				}

				result.SetResult(CommandResult.Success(
					successMessage,
					SessionActivationJson(slot)));
			} catch (Exception ex) {
				result.SetException(slot is null
					? ex
					: RollbackSessionLoad(slot, removeSlot: true, error: ex));
			}
		});
		return result.Task;
	}

	private Task<CommandResult> LoadExistingAsync(SessionSlot slot, string branch) {
		var result = new TaskCompletionSource<CommandResult>();
		_ui.Post(() => {
			try {
				LoadSlotInBackground(slot);
				result.SetResult(CommandResult.Success(
					$"Loaded the existing session for '{branch}'.",
					SessionActivationJson(slot)));
			} catch (Exception ex) {
				result.SetException(ex);
			}
		});
		return result.Task;
	}

	private static string SessionAddressJson(SessionSlot slot) {
		var address = slot.Session?.Address
			?? throw new InvalidOperationException("A dormant session has no live address.");
		return JsonSerializer.Serialize(new {
			id = slot.Id,
			address = new {
				slot = address.Slot,
				incarnation = address.Incarnation,
			},
		});
	}

	private static string SessionActivationJson(SessionSlot slot) {
		var address = slot.Session?.Address
			?? throw new InvalidOperationException("A dormant session has no live address.");
		return JsonSerializer.Serialize(new {
			id = slot.Id,
			address = new {
				slot = address.Slot,
				incarnation = address.Incarnation,
			},
			activateSession = true,
		});
	}

	private async Task<string> ResolveBaseRefAsync(
		HostSession source,
		string? baseSpec,
		CancellationToken ct) {
		var git = new GitService();
		if (string.IsNullOrWhiteSpace(baseSpec)
			|| string.Equals(baseSpec, "source", StringComparison.OrdinalIgnoreCase)) {
			return await git.GetHeadCommitAsync(source.WorkspaceRoot, ct).ConfigureAwait(false);
		}

		if (string.Equals(baseSpec, "main", StringComparison.OrdinalIgnoreCase)) {
			return await git.ResolveDefaultBranchAsync(WorkspaceRoot, ct).ConfigureAwait(false)
				?? await git.GetHeadCommitAsync(WorkspaceRoot, ct).ConfigureAwait(false);
		}

		throw new InvalidOperationException($"Unknown session base '{baseSpec}'.");
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

	// Seed the agent's first prompt once the runtime has had a moment to attach. Best-effort; not load-bearing.
	private static void SeedFirstPrompt(HostSession session, string prompt) {
		_ = session.Background.Run(async ct => {
			await Task.Delay(2500, ct).ConfigureAwait(false);
			session.SendAgentPrompt(prompt);
		});
	}
}
