using System.Text.Json;
using Weavie.Core;
using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Git;
using Weavie.Core.Sessions;
using Weavie.Core.Workspaces;
using Weavie.Core.Worktrees;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

// HostCore's worktree/slot orchestration: one SessionSlot per checkout, each loaded or dormant.
public sealed partial class HostCore {
	/// <summary>Wires behavior to the owning session bus. No callback observes client selection.</summary>
	private void WireSession(HostSession session) {
		AttachGitStatus(session);
		AttachPullRequestStatus(session);
		session.EditorSessionChanged += state => {
			if (SlotFor(session) is { } slot) {
				slot.EditorSession = state;
				PersistSessionState();
			}
		};
		session.Editor.Changed += editor => RecordRecentFile(session, editor);
		session.Commands.WebInvoker = (id, args, ct) => InvokeWebCommandAsync(session, id, args, ct);
		session.Commands.ClientInvoker = (id, args, ct) => InvokeClientCommandAsync(session, id, args, ct);
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
		SessionCommands.RegisterHandlers(session.Commands, new BoundSessionHost(this, session));
		WireCoreSessionMessages(session);

		WireFileActivity(session);
		session.Changes.AcceptedCommitted += paths => PostForSession(session, () => {
			// History before diff/changes — see HostCore.WebBridge.ApplyHistoryResult's doc comment on why.
			PushReviewHistoryToWeb(session);
			PushTurnChangesToWeb(session);
			foreach (string path in paths) {
				PushTurnDiffToWeb(session, path);
			}
		});
		WireAttention(session);
		session.Status.Changed += status => {
			session.PullRequestStatus.UpdateStatus(status);
			PostForSession(session, () => {
				PostSessionStatus(session, status);
				PushGitStatus(session);

				if (Draining) {
					EvaluateDrain();
				}

				PushSessionList();
			});
		};
	}

	private void PostForSession(HostSession session, Action action) {
		_ = session.Background.Run(ct => _ui.InvokeAsync(() => {
			if (!ct.IsCancellationRequested) {
				action();
			}

			return Task.CompletedTask;
		}, ct));
	}

	/// <summary>Test seam for one exact logical slot.</summary>
	internal HostSession? SessionForTest(string slot) =>
		_sessions?.Find(slot)?.Session;

	/// <summary>Test seam for the ordinary session attached to the user-owned workspace checkout.</summary>
	internal HostSession? WorkspaceSessionForTest =>
		_sessions?.Slots.FirstOrDefault(slot =>
			!slot.ManagedCheckout && PathsEqual(slot.WorktreePath, WorkspaceRoot))?.Session;

	/// <summary>Every loaded session's live backend, in catalog order.</summary>
	private List<HostSession> LoadedSessions() {
		var list = new List<HostSession>();
		if (_sessions is not null) {
			foreach (var slot in _sessions.Slots) {
				if (slot.Session is { } session) {
					list.Add(session);
				}
			}
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
			() => _settings.RequireString(CoreSettings.WorktreeSetupCommand, WorkspaceRoot),
			() => _settings.RequireString(CoreSettings.WorktreeTeardownCommand, WorkspaceRoot));
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

	/// <summary>Creates the ordinary session attached to the user-owned workspace checkout when it is absent.</summary>
	private void EnsureWorkspaceSession() {
		var sessions = _sessions
			?? throw new InvalidOperationException("The session catalog is not initialized.");
		if (sessions.Slots.Any(slot => !slot.ManagedCheckout && PathsEqual(slot.WorktreePath, WorkspaceRoot))) {
			return;
		}

		string id = SessionId.New().Value;
		var session = CreateSession(WorkspaceRoot, "claude", id);
		session.DisplayLabel = _workspaceSessionLabel;
		var slot = new SessionSlot {
			Id = id,
			Label = _workspaceSessionLabel,
			WorktreePath = WorkspaceRoot,
			ManagedCheckout = false,
			AgentProviderId = "claude",
			Session = session,
			EditorSession = EditorSession.Empty,
		};
		sessions.Add(slot);
		session.Scratch.GarbageCollect([]);
		ActivateSessionRuntimeAndMessages(session);
		PushSessionList();
		PersistSessionState();
	}

	/// <summary>
	/// Reconciles the worktree registry against real git, then adds an UNLOADED slot for every existing
	/// non-root worktree so it appears on the rail (faded) instead of leaking invisibly. Orphans are skipped.
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
					ManagedCheckout = status.IsManaged,
					AgentProviderId = agentProviderId,
					Session = null,
					EditorSession = EditorSession.Empty,
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

	private void ActivateSessionRuntimeAndMessages(HostSession session) {
		SyncSession(session, session.Bus.BroadcastTarget);
		session.ActivateOwnedRuntimeAndMessages();
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
		string ProviderId,
		string AgentSurface,
		int AgentInputProtocol,
		string Status,
		int Hue,
		string Monogram);

	private async Task<string> ResolveWorkspaceSessionLabelAsync(GitService git, bool isRepo) {
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
				(userInitiated, accept) => TryAcceptInput(session!.SlotId, userInitiated, accept),
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
			slot.Session.EditorSession = slot.EditorSession;
			slot.Session.Scratch.GarbageCollect(
				slot.EditorSession.Open.Where(entry => entry.Scratch).Select(entry => entry.Path));
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
			ActivateSessionRuntimeAndMessages(session);
			PersistSessionState();
			// Start Claude now even before its pane mounts (else it spawns on terminal ready); structured runtimes
			// already started with their owned endpoint. The resize nudge on first mount repaints the live TUI.
			session.Claude?.EnsureStarted();
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
		SessionSlot? source,
		NewSessionRequest request,
		CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(request);
		if (!TryDecodeInitialInput(request.Prompt, request.Attachments, out var input, out string error)) {
			return Task.FromResult(CommandResult.Failure(error));
		}
		string provider = ResolveNewSessionProvider(request.AgentProviderId);
		return RunSessionLifecycleAsync(
			() => request.Existing
				? AttachExistingSessionAsync(request.Branch, input, provider, ct)
				: CreateWorktreeSessionAsync(source, request.Branch, request.Base, input, provider, ct),
			ct);
	}

	private Task<CommandResult> ForkSessionAsync(
		HostSession source,
		ForkSessionRequest request,
		CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(request);
		string providerId = SlotFor(source)?.AgentProviderId ?? ResolveNewSessionProvider(null);
		var input = InitialSessionInput.FromText(request.Handoff);
		return RunSessionLifecycleAsync(
			() => CreateWorktreeSessionAsync(SlotFor(source), request.Branch, "source", input, providerId, ct),
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

		var result = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
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
		HostSession? source,
		string? sessionId,
		CommandInvocationContext context,
		CancellationToken ct) =>
		await RunSessionLifecycleAsync(
			() => UnloadSessionCoreAsync(source, sessionId, context, ct),
			ct).ConfigureAwait(false);

	private async Task<CommandResult> UnloadSessionCoreAsync(
		HostSession? source,
		string? sessionId,
		CommandInvocationContext context,
		CancellationToken ct) {
		ct.ThrowIfCancellationRequested();
		var target = string.IsNullOrWhiteSpace(sessionId) ? null : _sessions?.Find(sessionId);
		if (target is null) {
			return CommandResult.Failure("No such session.");
		}

		if (!target.Loaded) {
			return CommandResult.Success("That session is already unloaded.");
		}

		if (await FlushSessionViewAsync(target.Session!, ct).ConfigureAwait(false) is { } flushFailure) {
			return flushFailure;
		}

		if (ReferenceEquals(target.Session, source)) {
			context.AfterReply(ct => UnloadAfterReplyAsync(target, ct));
			return CommandResult.Success();
		}

		await UnloadSlotAndNotifyAsync(target, ct).ConfigureAwait(false);
		return CommandResult.Success();
	}

	private async Task UnloadAfterReplyAsync(SessionSlot target, CancellationToken ct) {
		try {
			await RunSessionLifecycleAsync(
				() => UnloadSlotAndNotifyAsync(target, ct),
				ct).ConfigureAwait(false);
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception ex) {
			Notify("error", $"Couldn't unload session '{target.Label}': {ex.Message}");
			throw;
		}
	}

	private async Task UnloadSlotAndNotifyAsync(SessionSlot target, CancellationToken ct) {
		if (await _ui.InvokeAsync(() => UnloadSlotAsync(target), ct).ConfigureAwait(false)) {
			Notify("info", $"Session '{target.Label}' was unloaded. Its worktree was kept.");
		}
	}

	private Task<CommandResult> DeleteSessionAsync(
		HostSession? source,
		string? sessionId,
		bool force,
		CommandInvocationContext context,
		CancellationToken ct) =>
		RunSessionLifecycleAsync(
			() => DeleteSessionCoreAsync(source, sessionId, force, context, ct),
			ct);

	private Task<CommandResult> DeleteSessionCoreAsync(
		HostSession? source,
		string? sessionId,
		bool force,
		CommandInvocationContext context,
		CancellationToken ct) {
		var target = string.IsNullOrWhiteSpace(sessionId) ? null : _sessions?.Find(sessionId);
		if (target is null) {
			return Task.FromResult(CommandResult.Failure("No such session."));
		}

		if (target.ManagedCheckout && _worktrees is not { }) {
			return Task.FromResult(CommandResult.Failure("This workspace isn't a git repository, so it has no worktree to delete."));
		}

		string worktreePath = target.WorktreePath;
		string label = target.Label;

		return DeleteSessionAfterValidationAsync(
			source,
			target,
			worktreePath,
			label,
			force,
			context,
			ct);
	}

	private async Task<CommandResult> DeleteSessionAfterValidationAsync(
		HostSession? source,
		SessionSlot target,
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
			if (target.ManagedCheckout && !force && IsLiveWorktree(worktreePath)
				&& await new GitService().HasUncommittedChangesAsync(worktreePath, ct).ConfigureAwait(false)) {
				return CommandResult.Failure(
					$"Session '{label}' has uncommitted changes; deleting would discard them. Re-run with force to delete anyway.");
			}
		} catch (GitException ex) {
			return CommandResult.Failure($"Couldn't delete session '{label}': {ex.Message}");
		}

		if (target.Session is { } targetSession && ReferenceEquals(targetSession, source)) {
			context.AfterReply(ct => DeleteAfterReplyAsync(target, worktreePath, label, force, ct));
			return CommandResult.Success();
		}

		return await DeleteAfterPreflightAsync(target, worktreePath, label, force, ct).ConfigureAwait(false);
	}

	private async Task DeleteAfterReplyAsync(
		SessionSlot target,
		string worktreePath,
		string label,
		bool force,
		CancellationToken ct) {
		try {
			var result = await RunSessionLifecycleAsync(
				() => DeleteAfterPreflightAsync(target, worktreePath, label, force, ct),
				ct).ConfigureAwait(false);
			if (!result.Ok) {
				Notify("error", result.Error ?? $"Couldn't delete session '{label}'.");
			}
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception ex) {
			Notify("error", $"Couldn't delete session '{label}': {ex.Message}");
			throw;
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
		string worktreePath,
		string label,
		bool force,
		CancellationToken admissionCancellation) {
		try {
			if (!ReferenceEquals(_sessions?.Find(target.Id), target)) {
				return CommandResult.Success($"Session '{label}' is already deleted.");
			}

			// Tear the live backend down first so no process holds the worktree dir, then remove the worktree
			// (keeping the branch). The unload starts on the UI thread to mutate the slot and this method awaits
			// its teardown from off it. Past the dirty guard deletion is deliberately uncancellable: self-delete
			// tears down the endpoint that accepted the command, and git must not be interrupted mid-removal.
			if (target.Loaded) {
				await _ui.InvokeAsync(() => UnloadSlotAsync(target), admissionCancellation).ConfigureAwait(false);
			}

			if (target.ManagedCheckout) {
				// Settle before removal: Windows can lag on releasing the unloaded children's handles, and external
				// scanners may briefly hold a lock. A short pause lets git's one-shot remove succeed instead of
				// partial-failing and orphaning the directory (git deletes its own record mid-failure, unrecoverable).
				await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
				await _worktrees!.RemoveAsync(
					worktreePath,
					deleteBranch: false,
					force,
					CancellationToken.None).ConfigureAwait(false);
			}
			// Back on the UI thread for the slot-set mutation + rail push (the awaits above left it), so the
			// removal can't interleave with a concurrent switch reading the slot set.
			await _ui.InvokeAsync(() => {
				_sessions?.Remove(target);
				if (_sessions?.Slots.Count == 0) {
					EnsureWorkspaceSession();
				} else {
					PushSessionList();
					PersistSessionState();
				}
				return Task.CompletedTask;
			}, CancellationToken.None).ConfigureAwait(false);
			Notify("info", target.ManagedCheckout
				? $"Session '{label}' was deleted. Its branch was kept."
				: $"Session '{label}' was deleted.");
			return CommandResult.Success();
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

	private async Task<CommandResult> ClassifyDeleteAsync(string? sessionId, CancellationToken ct) {
		var target = string.IsNullOrWhiteSpace(sessionId) ? null : _sessions?.Find(sessionId);
		if (target is null) {
			return CommandResult.Failure("No such session.");
		}

		// A gone/half-removed worktree (no .git) can't be inspected and has nothing left to lose — classify clean.
		string state = "clean";
		IReadOnlyList<string> tracked = [];
		IReadOnlyList<string> untracked = [];
		if (target.ManagedCheckout && IsLiveWorktree(target.WorktreePath)) {
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
			removesCheckout = target.ManagedCheckout,
			changedFiles = changed.Take(previewLimit).ToArray(),
			changedCount = changed.Length,
		}));
	}

	/// <summary>Tears down a slot's live backend, leaving its worktree as a dormant catalog entry.</summary>
	private async Task<bool> UnloadSlotAsync(SessionSlot slot) {
		if (slot.Session is not { } session) {
			return false;
		}

		await session.DisposeAsync().ConfigureAwait(false);
		return await _ui.InvokeAsync(() => {
			if (ReferenceEquals(slot.Session, session)) {
				slot.Session = null;
				_mediaRoutes.Unregister(session.Incarnation);
				PushSessionList();
				PersistSessionState();
				return Task.FromResult(true);
			}

			return Task.FromResult(false);
		}, CancellationToken.None).ConfigureAwait(false);
	}

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
		SessionSlot? source,
		string? requestedBranch,
		string? baseSpec,
		InitialSessionInput? input,
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

		if (string.IsNullOrWhiteSpace(requestedBranch)) {
			return CommandResult.Failure("Type a branch name to create a session.");
		}

		string branch = requestedBranch.Trim();
		// The branch name is web-supplied; reject a malformed/option-shaped name before it reaches git.
		try {
			if (!await new GitService().IsValidBranchNameAsync(
				source?.WorktreePath ?? WorkspaceRoot,
				branch,
				ct).ConfigureAwait(false)) {
				return CommandResult.Failure($"'{branch}' isn't a valid branch name.");
			}
		} catch (GitException ex) {
			return CommandResult.Failure($"Couldn't validate the branch name: {ex.Message}");
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
			input,
			agentProviderId,
			$"Created session on branch '{branch}' at {record.Path}.").ConfigureAwait(false);
	}

	/// <summary>
	/// Creates a session by checking out an existing branch into a new worktree. If Weavie already has a session
	/// for that branch — or it's the workspace checkout's own branch — switches to that instead of duplicating.
	/// </summary>
	private async Task<CommandResult> AttachExistingSessionAsync(
		string? requestedBranch,
		InitialSessionInput? input,
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
			if (ExistingSessionInputError(input) is { } error) {
				return error;
			}
			return await LoadExistingAsync(existingSlot, branch).ConfigureAwait(false);
		}

		// The branch checked out in the workspace root can't be attached to a second worktree (git refuses), so
		// load the ordinary session already attached to that checkout.
		try {
			string? workspaceBranch = await new GitService().GetCurrentBranchAsync(WorkspaceRoot, ct).ConfigureAwait(false);
			var workspaceSlot = _sessions?.Slots.FirstOrDefault(slot =>
				!slot.ManagedCheckout && PathsEqual(slot.WorktreePath, WorkspaceRoot));
			if (string.Equals(workspaceBranch, branch, StringComparison.Ordinal) && workspaceSlot is not null) {
				if (ExistingSessionInputError(input) is { } error) {
					return error;
				}
				return await LoadExistingAsync(workspaceSlot, branch).ConfigureAwait(false);
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

		// Seed the first input only on this fresh-checkout path; switching to an existing session (above) must
		// never re-seed it. The Open-PR flow uses this to brief Claude on the PR it just checked out.
		return await BuildSlotAsync(
			branch,
			record,
			input,
			slotProviderId,
			$"Checked out '{branch}' at {record.Path}.").ConfigureAwait(false);
	}

	private static CommandResult? ExistingSessionInputError(InitialSessionInput? input) =>
		input is null
			? null
			: CommandResult.Failure("A prompt or images can't be submitted when opening a session that already exists.");

	/// <summary>
	/// Builds a <see cref="SessionSlot"/> for a worktree <paramref name="record"/>, adds it to the rail, and
	/// returns its exact address so the calling page can select it (optionally seeding its first input).
	/// </summary>
	private Task<CommandResult> BuildSlotAsync(
		string branch,
		WorktreeRecord record,
		InitialSessionInput? input,
		string agentProviderId,
		string successMessage) {
		var sessions = _sessions
			?? throw new InvalidOperationException("The session catalog is not initialized.");
		var result = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		_ui.Post(() => {
			SessionSlot? slot = null;
			try {
				slot = new SessionSlot {
					Id = branch,
					Label = branch,
					WorktreePath = record.Path,
					ManagedCheckout = true,
					AgentProviderId = agentProviderId,
					Session = CreateSession(record.Path, agentProviderId, branch),
					EditorSession = EditorSession.Empty,
				};
				sessions.Add(slot);
				PushSessionList();
				if (input is not null) {
					slot.Session.QueueInitialInput(MaterializeInitialInput(slot.Session, input));
				}
				ActivateSessionRuntimeAndMessages(slot.Session);
				PersistSessionState();

				result.SetResult(CommandResult.Success(
					successMessage,
					CreatedSessionActivationJson(slot)));
			} catch (Exception ex) {
				result.SetException(slot is null
					? ex
					: RollbackSessionLoad(slot, removeSlot: true, error: ex));
			}
		});
		return result.Task;
	}

	private static bool TryDecodeInitialInput(
		string? prompt,
		IReadOnlyList<NewSessionAttachment> attachments,
		out InitialSessionInput? input,
		out string error) {
		ArgumentNullException.ThrowIfNull(attachments);
		try {
			var ids = new HashSet<string>(StringComparer.Ordinal);
			var decoded = new List<InitialSessionAttachment>(attachments.Count);
			foreach (var attachment in attachments) {
				if (string.IsNullOrWhiteSpace(attachment.Id)) {
					throw new InvalidOperationException("A new-session image is missing its attachment id.");
				}
				if (!ids.Add(attachment.Id)) {
					throw new InvalidOperationException($"Attachment '{attachment.Id}' was included more than once.");
				}

				var (extension, bytes) = PastedImageMedia.Decode(attachment.Mime, attachment.DataB64);
				decoded.Add(new InitialSessionAttachment(attachment.Id, attachment.Mime, extension, bytes));
			}

			input = InitialSessionInput.Create(prompt, decoded);
			error = string.Empty;
			return true;
		} catch (Exception ex) when (ex is FormatException or InvalidOperationException) {
			input = null;
			error = ex.Message;
			return false;
		}
	}

	private static AgentTurnSubmission MaterializeInitialInput(
		HostSession session,
		InitialSessionInput input) =>
		new() {
			Id = Guid.NewGuid().ToString("n"),
			Text = input.Text,
			Attachments = [.. input.Attachments.Select(attachment => new AgentInputAttachment {
				Id = attachment.Id,
				Mime = attachment.Mime,
				Path = session.PastedImages.Write(attachment.Extension, attachment.Bytes),
			})],
			Skills = [],
		};

	private sealed record InitialSessionAttachment(
		string Id,
		string Mime,
		string Extension,
		byte[] Bytes);

	private sealed record InitialSessionInput(string Text, IReadOnlyList<InitialSessionAttachment> Attachments) {
		public static InitialSessionInput? Create(
			string? text,
			IReadOnlyList<InitialSessionAttachment> attachments) =>
			string.IsNullOrWhiteSpace(text) && attachments.Count == 0
				? null
				: new InitialSessionInput(text?.Trim() ?? string.Empty, attachments);

		public static InitialSessionInput? FromText(string? text) => Create(text, []);
	}

	private Task<CommandResult> LoadExistingAsync(SessionSlot slot, string branch) {
		var result = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
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
		return JsonSerializer.Serialize(new {
			id = slot.Id,
			address = LiveAddress(slot),
			activateSession = true,
		});
	}

	private static string CreatedSessionActivationJson(SessionSlot slot) =>
		JsonSerializer.Serialize(new {
			id = slot.Id,
			address = LiveAddress(slot),
			activateSession = true,
			createdSession = true,
		});

	private static object LiveAddress(SessionSlot slot) {
		var address = slot.Session?.Address
			?? throw new InvalidOperationException("A dormant session has no live address.");
		return new {
			slot = address.Slot,
			incarnation = address.Incarnation,
		};
	}

	private async Task<string> ResolveBaseRefAsync(
		SessionSlot? source,
		string? baseSpec,
		CancellationToken ct) {
		var git = new GitService();
		if (string.IsNullOrWhiteSpace(baseSpec)
			|| string.Equals(baseSpec, "source", StringComparison.OrdinalIgnoreCase)) {
			if (source is null) {
				throw new InvalidOperationException("Pick a source session or branch from main.");
			}

			return await git.GetHeadCommitAsync(source.WorktreePath, ct).ConfigureAwait(false);
		}

		if (string.Equals(baseSpec, "main", StringComparison.OrdinalIgnoreCase)) {
			return await git.ResolveDefaultBranchAsync(WorkspaceRoot, ct).ConfigureAwait(false)
				?? await git.GetHeadCommitAsync(WorkspaceRoot, ct).ConfigureAwait(false);
		}

		throw new InvalidOperationException($"Unknown session base '{baseSpec}'.");
	}

}
