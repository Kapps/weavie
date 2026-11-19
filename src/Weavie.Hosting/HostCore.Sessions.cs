using System.Text.Json;
using Weavie.AcpDistribution;
using Weavie.Core;
using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Git;
using Weavie.Core.Inference;
using Weavie.Core.Sessions;
using Weavie.Core.Workflows;
using Weavie.Core.Workspaces;
using Weavie.Core.Worktrees;
using Weavie.Hosting.Messaging;
using Weavie.Workflows;

namespace Weavie.Hosting;

// HostCore's worktree/slot orchestration: one SessionSlot per checkout, each loaded or dormant.
public sealed partial class HostCore {
	/// <summary>Wires behavior to the owning session bus. No callback observes client selection.</summary>
	private void WireSession(HostSession session) {
		// Off the raising thread (any observer's, including the UI's): closing a session is lifecycle work.
		session.WorkspaceRootVanished += () => _ = Task.Run(() => CloseVanishedSessionAsync(session));
		// Both monitors exist before either runs: each refreshes from the other (branch commits read the PR's base).
		AttachPullRequestStatus(session);
		AttachGitStatus(session);
		session.PullRequestStatus.UpdateStatus(session.Status.Status);
		if (session.Agent.Controls is { } controls) {
			string provider = session.Agent.Provider.Id;
			controls.ControlStateChanged += state => {
				if (state.Ready) _agentModels.Observe(provider, state.Axes, AgentModelSource.Session);
			};
		}
		session.EditorSessionChanged += state => {
			if (SlotFor(session) is { } slot) {
				slot.EditorSession = state;
				PersistSessionState();
			}
		};
		session.Editor.Changed += editor => RecordRecentFile(session, editor);
		session.Commands.WebInvoker = (id, args, ct) => InvokeWebCommandAsync(session, id, args, ct);
		session.Commands.ClientInvoker = (id, args, ct) => InvokeClientCommandAsync(session, id, args, ct);
		RegisterShellTerminalHandlers(session);
		// Succeeds only once the restart has happened, so the caller never acts on the agent it replaced.
		session.Commands.RegisterHandler(CoreCommands.RestartAgent, (argsJson, ct) =>
			BackgroundWorkRefusal(session, StopsBackgroundWork(argsJson)) is { } refusal
				? Task.FromResult(refusal)
				: _ui.InvokeAsync(() => {
					try {
						session.RestartAgent();
						return Task.FromResult(CommandResult.Success("Restarted the agent."));
					} catch (Exception ex) when (ex is IOException or InvalidOperationException) {
						return Task.FromResult(CommandResult.Failure(ex.Message));
					}
				}, ct));
		session.Commands.RegisterHandler(CoreCommands.RestartLanguageServers, (_, ct) => _ui.InvokeAsync(() => {
			session.LspRevision++;
			PushLspConfigToWeb(session);
			return Task.FromResult(CommandResult.Success("Restarting language servers."));
		}, ct));
		session.Commands.RegisterHandler(CoreCommands.ClearAgentConversation, (argsJson, _) => {
			try {
				if (BackgroundWorkRefusal(session, StopsBackgroundWork(argsJson)) is { } refusal) return Task.FromResult(refusal);
				session.StartNewAgentConversation();
				return Task.FromResult(CommandResult.Success("Started a fresh agent conversation."));
			} catch (Exception ex) when (ex is IOException or InvalidOperationException) {
				return Task.FromResult(CommandResult.Failure(ex.Message));
			}
		});
		session.Commands.RegisterHandler(CoreCommands.AskAgentAside, (argsJson, _) => {
			try {
				if (session.Agent.SideConversations is not { } sideConversations) {
					return Task.FromResult(CommandResult.Failure(
						"This agent does not support context-preserving side conversations."));
				}
				if (_drainInputFrozen) throw new InvalidOperationException("Agent input is paused while Weavie restarts.");
				var args = JsonSerializer.Deserialize(argsJson ?? "{}", WireJson.Default.AgentAsideCommand)
					?? throw new ArgumentException("Ask Agent Aside requires a question or image.");
				session.AcceptAgentSubmission(new HostSession.AgentSubmitMessage(
					args.SubmissionId ?? Guid.NewGuid().ToString("N"), args.Question ?? string.Empty,
					args.Kind ?? "prompt", args.CommandName ?? string.Empty, args.AttachmentIds, args.Selections), sideConversations.AskAside);
				return Task.FromResult(CommandResult.Success());
			} catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException) {
				return Task.FromResult(CommandResult.Failure(ex.Message));
			}
		});
		session.Commands.RegisterHandler(CoreCommands.RewindAgentConversation, async (argsJson, _) => {
			try {
				if (session.Agent.Rewind is not { } rewind) return CommandResult.Failure("This agent cannot rewind its conversation.");
				if (_drainInputFrozen) throw new InvalidOperationException("Agent input is paused while Weavie restarts.");
				if (BackgroundWorkRefusal(session, StopsBackgroundWork(argsJson)) is { } refusal) return refusal;
				string? turnId = JsonSerializer.Deserialize(argsJson ?? "{}", WireJson.Default.AgentRewindCommand)?.TurnId;
				await (turnId is null ? rewind.RewindLatestAsync() : rewind.RewindBeforeAsync(turnId)).ConfigureAwait(false);
				return CommandResult.Success("Rewound the agent conversation.");
			} catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or IOException) {
				return CommandResult.Failure(ex.Message);
			}
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
		RegisterCredentialHandlers(session);
		RegisterDecisionProviderHandler(session);
		session.Commands.RegisterHandler(CoreCommands.ResetCommandLearning, (_, _) => Task.FromResult(ResetCommandLearning()));
		InferenceCommands.RegisterHandlers(session.Commands, _settings);
		// View Logs: snapshot the captured console output into a read-only tab + return the recent tail (see
		// HostCore.Logs.cs). The tab opens on the invoking session's bus.
		session.Commands.RegisterHandler(CoreCommands.ViewLogs, (_, _) => Task.FromResult(ShowLogs(session)));
		RegisterTestRunHandlers(session);
		RegisterAgentBackgroundHandlers(session);
		SessionCommands.RegisterHandlers(session.Commands, new BoundSessionHost(this, session));
		WireCoreSessionMessages(session);
		WireReviewHints(session);
		WireWorkflow(session);
		WireSpelling(session);

		WireFileActivity(session);
		WireAttention(session);
		WireActivity(session);
		// Delivered serially by the machine, so this closure-tracked previous status can't race its own handler.
		var previous = session.Status.Status;
		session.Status.Changed += status => {
			session.PullRequestStatus.UpdateStatus(status);
			bool marksActive = SessionStatusMachine.MarksLastActive(previous, status);
			bool finished = AttentionRules.Classify(previous, status) == AttentionKind.TurnComplete;
			previous = status;
			PostForSession(session, () => {
				if (marksActive && OwnedSlot(session) is { } slot) {
					slot.LastActiveAt = _time.GetUtcNow();
					slot.Unseen |= finished;
					PersistSessionState();
				}

				PostSessionStatus(session, status);
				PushGitStatus(session);

				if (Draining) {
					EvaluateDrain();
				}

				PushSessionList();
			});
		};
	}

	internal sealed record AgentAsideCommand(
		string? Question, string? SubmissionId, string[]? AttachmentIds, HostSession.AgentSelectionWire[]? Selections, string? Kind, string? CommandName);
	internal sealed record AgentRewindCommand(string? TurnId);

	private void PostForSession(HostSession session, Action action) {
		_ = session.Background.Run(ct => _ui.InvokeAsync(() => {
			if (!ct.IsCancellationRequested) {
				action();
			}

			return Task.CompletedTask;
		}, ct));
	}

	/// <summary>The slot <paramref name="session"/> is still the live backend of, or <c>null</c> once it was replaced.</summary>
	private SessionSlot? OwnedSlot(HostSession session) =>
		_sessions?.Find(session.SlotId) is { } slot && ReferenceEquals(slot.Session, session) ? slot : null;

	/// <summary>Test seam for the live session of the slot with this id or branch label.</summary>
	internal HostSession? SessionForTest(string slot) =>
		_sessions?.Find(SlotIdForTest(slot) ?? slot)?.Session;

	/// <summary>Test seam for the id of the slot labelled <paramref name="label"/>.</summary>
	internal string? SlotIdForTest(string label) => SlotForTest(label)?.Id;

	/// <summary>The slot labelled <paramref name="label"/>, loaded or not.</summary>
	internal SessionSlot? SlotForTest(string label) =>
		_sessions?.Slots.FirstOrDefault(slot => string.Equals(slot.Label, label, StringComparison.Ordinal));

	/// <summary>Test seam for the ordinary session attached to the user-owned workspace checkout.</summary>
	internal HostSession? WorkspaceSessionForTest =>
		_sessions?.Slots.FirstOrDefault(IsWorkspaceCheckout)?.Session;

	/// <summary>The session on the workspace's home folder.</summary>
	private bool IsWorkspaceCheckout(SessionSlot slot) => IsWorkspaceCheckout(slot.WorktreePath);

	/// <summary>A session on its repo's own folder: no worktree a repo here tracks, so deleting the session leaves the folder.</summary>
	private static bool KeepsFolder(SessionSlot slot) => PathIdentity.Equals(slot.WorktreePath, slot.Repo.Root);

	private bool IsWorkspaceCheckout(string worktreePath) => PathIdentity.Equals(worktreePath, HomeFolder);

	/// <summary>The worktree git has <paramref name="branch"/> checked out in, if any.</summary>
	private static async Task<string?> WorktreeOnBranchAsync(Repo repo, string branch, CancellationToken ct) =>
		(await new GitService().ListWorktreesAsync(repo.Root, ct).ConfigureAwait(false))
			.FirstOrDefault(worktree => string.Equals(worktree.Branch, branch, StringComparison.Ordinal))?.Path;

	/// <summary>The session on the worktree git has <paramref name="branch"/> checked out in, loaded or dormant.</summary>
	private async Task<SessionSlot?> SlotOnBranchAsync(Repo repo, string branch, CancellationToken ct) =>
		await WorktreeOnBranchAsync(repo, branch, ct).ConfigureAwait(false) is { } worktree
			? _sessions?.Find(SessionSlot.IdFor(worktree))
			: null;

	/// <summary>A typed repo path made absolute: relative to the session it was typed in, else to the home folder.</summary>
	private string RepoPath(string repo, SessionSlot? source) =>
		WorkspacePaths.Resolve(repo, source?.WorktreePath ?? HomeFolder);

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
			return (git, await git.IsRepositoryAsync(HomeFolder).ConfigureAwait(false));
		} catch (GitException) {
			return (git, false);
		}
	}

	/// <summary>Creates the session on the home folder, which has none; a failure leaves none behind.</summary>
	private SessionSlot OpenHomeSession(string providerId) {
		var sessions = _sessions
			?? throw new InvalidOperationException("The session catalog is not initialized.");
		var slot = new SessionSlot {
			Label = _workspaceSessionLabel,
			Device = NewDevice(null),
			Repo = Home,
			WorktreePath = HomeFolder,
			AgentProviderId = providerId,
			EditorSession = EditorSession.Empty,
			ShellTerminals = [ShellTerminalId.New()],
		};
		bool closed = _sessionStore.HomeClosed;
		try {
			var session = slot.Session = CreateSession(slot);
			sessions.Add(slot);
			session.Scratch.GarbageCollect([]);
			ActivateSessionRuntimeAndMessages(session);
			PushSessionList();
			_sessionStore.HomeClosed = false;
			PersistSessionState();
			return slot;
		} catch (Exception error) {
			_sessionStore.HomeClosed = closed;
			throw RollbackSessionLoad(slot, slot.Session, removeSlot: true, error);
		}
	}

	/// <summary>Brings back the home folder's session after it was deleted, or loads the one that is there.</summary>
	private Task<CommandResult> OpenHomeSessionAsync(CancellationToken ct) =>
		GatedAsync(_sessionCatalog, () => OpenHomeSessionCoreAsync(ResolveNewSessionProvider(null), ct), ct);

	// Under the catalog gate. The provider is only for a session created now: one that is there keeps its own.
	private async Task<CommandResult> OpenHomeSessionCoreAsync(string providerId, CancellationToken ct) {
		if (_sessions?.Slots.FirstOrDefault(IsWorkspaceCheckout) is { } existing) {
			return await LoadExistingAsync(existing, existing.Label, ct).ConfigureAwait(false);
		}

		try {
			_agentProviders.RequireAvailable(providerId);
		} catch (InvalidOperationException ex) {
			return CommandResult.Failure(ex.Message);
		}

		// The branch checked out there may have changed while it had no session.
		_workspaceSessionLabel = await ResolveWorkspaceSessionLabelAsync(new GitService(), Home.Worktrees is not null).ConfigureAwait(false);
		return await _ui.InvokeAsync(() => Task.FromResult(CommandResult.Success(
			$"Opened the session on {HomeFolder}.", CreatedSessionActivationJson(OpenHomeSession(providerId)))), ct).ConfigureAwait(false);
	}

	/// <summary>
	/// Reconciles the home repo's worktree registry against real git, then adds an UNLOADED slot for every existing
	/// non-root worktree so it appears on the rail (faded) instead of leaking invisibly. Orphans are skipped.
	/// </summary>
	private async Task ReconcileWorktreesOnOpenAsync() {
		if (Home.Worktrees is not { } worktrees || _sessions is null) {
			return;
		}

		try {
			var report = await worktrees.ReconcileAsync().ConfigureAwait(false);
			foreach (var status in report.Statuses) {
				if (!status.Exists || status.IsPrimary) {
					continue;
				}

				string label = status.Branch ?? Path.GetFileName(
					status.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
				string agentProviderId = ProviderFor(status);
				if (status is { IsManaged: true, Branch: not null }) {
					worktrees.RecoverOwnedRecord(status, agentProviderId);
				}
				if (_sessions.Find(SessionSlot.IdFor(status.Path)) is not null) {
					continue; // already surfaced
				}

				_sessions.Add(new SessionSlot {
					Label = label,
					Device = NewDevice(null),
					Repo = Home,
					WorktreePath = status.Path,
					AgentProviderId = agentProviderId,
					Session = null,
					EditorSession = EditorSession.Empty,
					ShellTerminals = [ShellTerminalId.New()],
				});
			}

			PushSessionList();
		} catch (GitException ex) {
			_worktreeReconciliationFailed = true;
			Console.WriteLine($"[weavie] worktree reconcile failed: {ex.Message}");
			Notify("warn", "Couldn't list existing worktrees — some sessions may not appear on the rail.");
		}
	}

	private string ProviderFor(WorktreeStatus status) {
		if (status.IsManaged) {
			return ProviderOrNull(status.AgentProviderId)
				?? PersistedProviderFor(status.Path)
				?? ResolveNewSessionProvider(null);
		}
		return PersistedProviderFor(status.Path) ?? ResolveNewSessionProvider(null);
	}

	private string? PersistedProviderFor(string worktreePath) =>
		ProviderOrNull(_sessionStore.Items
			.FirstOrDefault(item => PathIdentity.Equals(item.WorktreePath, worktreePath))?.AgentProviderId);

	private static string? ProviderOrNull(string? provider) =>
		string.IsNullOrWhiteSpace(provider) ? null : provider.Trim();

	/// <summary>
	/// The provider for the workspace's own session, which Weavie opens on the user's behalf. The standing
	/// default can name a provider that is gone or unusable (an ACP agent since removed), and that must not stop
	/// the workspace from opening: it falls to an available provider and says so at hello.
	/// </summary>
	private string AvailableWorkspaceSessionProvider() {
		string preferred = ResolveNewSessionProvider(null);
		if (_agentProviders.FindInfo(preferred) is { Available: true }) {
			return preferred;
		}

		var replacement = _agentProviders.Providers.Select(provider => provider.Info).FirstOrDefault(info => info.Available)
			?? throw new InvalidOperationException($"No agent provider is available to replace '{preferred}'.");
		_sessionStartupNotices.Add(
			ToastMessage.Plain("warn", $"Your default agent '{preferred}' isn't available — this session opened with {replacement.Name}."));
		return replacement.Id;
	}

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
	private void EnsureProviderCanBeRemoved(string providerId) {
		ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
		bool isDefault = string.Equals(
			_settings.RequireString(AgentSettings.DefaultProvider),
			providerId,
			StringComparison.Ordinal);
		bool isInferenceDefault = string.Equals(
			_settings.RequireString(InferenceSettings.DefaultProvider),
			providerId,
			StringComparison.Ordinal);
		bool hasSession = _sessionStore.Items.Any(item =>
			string.Equals(item.AgentProviderId, providerId, StringComparison.Ordinal))
			|| _sessions?.Slots.Any(slot =>
				string.Equals(slot.AgentProviderId, providerId, StringComparison.Ordinal)) == true;
		// Only the home repo lists worktrees with no saved session; every other repo's are covered by their slots.
		bool hasWorktree = Home.Worktrees?.Registry.Items.Any(item =>
			string.Equals(item.AgentProviderId, providerId, StringComparison.Ordinal)) == true;
		if (isDefault || isInferenceDefault || hasSession || hasWorktree) {
			throw new InvalidOperationException(
				$"ACP agent '{providerId}' is still referenced by a session, worktree, or default provider.");
		}
	}

	private void EnsureProvidersCanBeReplaced(
		IReadOnlySet<string> currentIds,
		IReadOnlySet<string> proposedIds) {
		foreach (string removed in currentIds.Where(id => !proposedIds.Contains(id))) {
			EnsureProviderCanBeRemoved(removed);
		}
	}

	/// <summary>Pushes the authoritative session catalog. Loaded entries carry their exact live address.</summary>
	private void PushSessionList() =>
		_messages.Host.Feature("sessions").Publish("catalog", WireJson.Default.SessionCatalogEntryArray, BuildSessionCatalog());

	private void ActivateSessionRuntimeAndMessages(HostSession session) {
		ReplaySession(session);
		session.ActivateOwnedRuntimeAndMessages();
	}

	private void ReplaySession(HostSession session) {
		SyncSession(session, session.Bus.BroadcastTarget);
		LogStartup($"session {session.SlotId}: state replayed");
	}

	private SessionCatalogEntry[] BuildSessionCatalog() {
		var slots = _sessions?.Slots ?? [];
		string[] repos = RepoRoots();
		return [.. slots
			.OrderByDescending(slot => slot.Loaded)
			.Select(slot => {
				var info = _agentProviders.FindInfo(slot.AgentProviderId);
				bool structured = info?.Capabilities
					.HasFlag(AgentProviderCapabilities.StructuredPane) == true;
				return new SessionCatalogEntry(
					slot.Id,
					slot.Label,
					SessionName.Title(slot.Label),
					slot.Session?.Address,
					slot.Loaded,
					slot.AgentProviderId,
					info is null ? "unavailable" : structured ? "structured" : "terminal",
					structured ? 2 : 0,
					slot.Session is { } session ? StatusName(session.Status.Status) : "idle",
					slot.LastActiveAt?.ToUnixTimeMilliseconds(),
					slot.Unseen,
					!KeepsFolder(slot) && slot.Repo.Worktrees?.Owns(slot.WorktreePath) == false,
					slot.Session?.Bus.ReloadReason,
					slot.Repo.Root,
					RepoLabel(slot.Repo.Root, repos),
					slot.WorktreePath);
			})];
	}

	/// <summary>A session as a message names it: its branch, led by its repo when the workspace's sessions span several.</summary>
	private string NameOf(SessionSlot slot) {
		return (_sessions?.Slots ?? []).Any(other => other.Repo != slot.Repo)
			? $"{RepoLabel(slot.Repo.Root, RepoRoots())} / {slot.Label}"
			: slot.Label;
	}

	private string NameOf(HostSession session) => SlotFor(session) is { } slot ? NameOf(slot) : session.DisplayLabel;

	// A repo's name among others: the shortest trailing part of its root that no other root shares.
	private static string RepoLabel(string root, IReadOnlyList<string> all) {
		static string Tail(string of, int segments) => string.Join('/', of
			.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
			.TakeLast(segments));
		int segments = 1;
		while (all.Any(other => other != root && Tail(other, segments) == Tail(root, segments)
			&& Tail(root, segments) != Tail(root, segments + 1))) {
			segments++;
		}

		return Tail(root, segments);
	}

	internal sealed record SessionCatalogEntry(
		string Id,
		string Label,
		string Title,
		SessionAddress? Address,
		bool Loaded,
		string ProviderId,
		string AgentSurface,
		int AgentInputProtocol,
		string Status,
		long? LastActiveAt,
		bool Unseen,
		bool External,
		string? ReloadRequired,
		string Repo,
		string RepoLabel,
		string Worktree);

	private async Task<string> ResolveWorkspaceSessionLabelAsync(GitService git, bool isRepo) {
		try {
			if (isRepo) {
				string? branch = await git.GetCurrentBranchAsync(HomeFolder).ConfigureAwait(false);
				if (!string.IsNullOrWhiteSpace(branch)) {
					return branch;
				}
			}
		} catch (GitException) {
			// Branch read failed → fall back to the folder name for the rail label.
		}

		return Path.GetFileName(HomeFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
	}

	private static void PostSessionStatus(HostSession session, SessionStatus status) =>
		PostSessionStatus(session.Bus.BroadcastTarget, status);

	private static void PostSessionStatus(MessageTarget target, SessionStatus status) =>
		target.Feature("status").Publish("changed", WireJson.Default.SessionStatusChanged, new(StatusName(status)));

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

	private readonly SessionServices _sessionServices;

	private SessionServices CreateSessionServices() => new() {
		Settings = _settings,
		Layout = _layout,
		CommandRegistry = _commandRegistry,
		Keybindings = _keybindings,
		ThemeOverrides = _themeOverrides,
		Inference = _inference,
		AgentConsultation = _agentConsultation,
		PtyLauncher = _platform.PtyLauncher,
		Runtime = _runtime,
		SettingsTools = _settingsTools,
		Workflow = new() {
			Roles = _workflowRoles,
			DocumentsDirectory = _workflowDocuments.ForRun,
			Decider = worktree => new ModelWorkflowDecider(_decisions, worktree, _decisionHistory),
			RoleResolver = RoleResolver,
		},
	};

	/// <summary>Builds + wires a new <see cref="HostSession"/>: the live backend for <paramref name="slot"/>.</summary>
	private HostSession CreateSession(SessionSlot slot) {
		var (repo, cwd, label, agentProviderId, shellTerminals) = (slot.Repo, slot.WorktreePath, slot.Label, slot.AgentProviderId, slot.ShellTerminals);
		string slotId = slot.Id;
		LogStartup($"session {slotId}: constructing");
		var provider = _agentProviders.RequireAvailable(agentProviderId);
		var address = new SessionAddress(slotId, Guid.NewGuid().ToString("n"));
		var endpoint = _messages.OpenSession(address);
		endpoint.Bus.ReloadRequiredChanged += () => _ui.Post(PushSessionList);
		HostSession? session = null;
		try {
			session = new HostSession(
				endpoint,
				provider,
				shellTerminals,
				SessionPaths.For(Id, repo, cwd),
				_sessionServices,
				new SessionOwner {
					Peers = new BoundSessionPeers(this, address, side: null),
					DeviceSettings = new BoundDeviceSettings(this, slot.Device),
					InputFrozen = () => _drainInputFrozen,
					AcceptTerminalInput = (userInitiated, accept) => TryAcceptInput(slotId, userInitiated, accept),
					ShellResized = _sessionStore.RecordShellSize,
				});

			// Seed every shell's pre-spawn size from the last real terminal size so a background-restored child is born at
			// the width its reattaching xterm will use — else its raw scrollback replays 80×24-wrapped and stacks garbled.
			if (_sessionStore.ShellSize is { } shellSize) {
				session.Shells.SeedSize(shellSize.Cols, shellSize.Rows);
			}

			session.DisplayLabel = label;
			TrackDevice(session, slot.Device);
			WireSession(session);
			_fileRoutes.Register(session.Incarnation, session.WorkspaceRoot);
			LogStartup($"session {slotId}: constructed");
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
			slot.Session = CreateSession(slot);
			slot.Session.InheritedWorkflowDocuments = slot.WorkflowDocumentsDirectory;
			RestoreSlotEditor(slot.Session, slot);
		}
	}

	private void RestoreSlotEditor(HostSession session, SessionSlot slot) {
		session.SeedEditorSession(slot.EditorSession);
		RestoreSourceDocuments(session);
		session.Scratch.GarbageCollect(
			slot.EditorSession.Open.Where(entry => entry.Scratch).Select(entry => entry.Path));
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
			StartSessionTerminals(session);
		} catch (Exception error) {
			throw RollbackSessionLoad(slot, slot.Session, removeSlot: false, error: error);
		}
	}

	private void StartSessionTerminals(HostSession session) {
		session.Claude?.EnsureStarted();
		session.Shells.EnsureStarted();
		LogStartup($"session {session.SlotId}: terminals started");
	}

	private Exception RollbackSessionLoad(
		SessionSlot slot,
		HostSession? session,
		bool removeSlot,
		Exception error) {
		var failures = new List<Exception> { error };
		if (session is not null) {
			try {
				session.DisposeAsync().AsTask().GetAwaiter().GetResult();
			} catch (Exception cleanupError) {
				failures.Add(cleanupError);
			}

			if (ReferenceEquals(slot.Session, session)) {
				slot.Session = null;
			}
			_fileRoutes.Unregister(session.Incarnation);
		}

		if (removeSlot) {
			RemoveSlot(slot);
		}

		try {
			PushSessionList();
			PersistSessionState();
			if (removeSlot) {
				ForgetWorkflowRun(slot.WorktreePath);
				_workflowDocuments.Bind(slot.WorktreePath, null);
			}
		} catch (Exception catalogError) {
			failures.Add(catalogError);
		}

		return failures.Count == 1
			? error
			: new AggregateException("Session load and rollback both failed.", failures);
	}

	// A session takes the device of the page that created it, or of the session an agent created it from.
	private static DeviceIdentity? SourceDevice(SessionSlot? source) => source?.Device.Current.Home;

	private Task<CommandResult> NewSessionAsync(
		SessionAddress? sourceAddress,
		NewSessionRequest request,
		Func<SessionSlot?, DeviceIdentity?> device,
		CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(request);
		if (!TryDecodeInitialInput(request.Prompt, request.Attachments, out var input, out string error)) {
			return Task.FromResult(CommandResult.Failure(error));
		}
		string provider = ResolveNewSessionProvider(request.AgentProviderId);
		_global.RememberDefaultProvider(provider);
		return GatedAsync(_sessionCatalog, async () => {
			SessionSlot? source = null;
			if (sourceAddress is not null && (source = await CurrentSourceAsync(sourceAddress, ct).ConfigureAwait(false)) is null) {
				return SourceGone();
			}
			// Nothing stands in for an unnamed repo: a start from no session has to name one.
			if (string.IsNullOrWhiteSpace(request.Repo) && source is null) {
				return CommandResult.Failure("A session started from no session needs its 'repo'.");
			}

			Repo repo;
			try {
				repo = source is not null && string.IsNullOrWhiteSpace(request.Repo)
					? source.Repo
					: await RepoForAsync(RepoPath(request.Repo!, source), ct).ConfigureAwait(false);
			} catch (GitException ex) {
				return CommandResult.Failure(ex.Message);
			}

			using var documents = _workflowDocuments.Acquire(source?.WorktreePath);
			if (request.Workflow is null && request.Roles is not null) {
				return CommandResult.Failure("'roles' needs a 'workflow'.");
			}
			if (request.Workflow is { } workflow) {
				// The session's controls don't exist yet, so only its provider and saved roles can refuse before a worktree does.
				try {
					var picks = RoleResolver(RequireProvider(provider)).Picking(WorkflowRolePick.Read(request.Roles));
					input = InitialSessionInput.ForWorkflow(WeavieWorkflows.Catalog(LaunchRoot(source, repo.Root)).Prepare(
						workflow, request.Inputs ?? EmptyInputs, documents.Directory, InitialSessionInput.WorkflowPrompt(input), picks));
					picks.Save();
				} catch (WorkflowException ex) {
					return CommandResult.Failure(ex.Message);
				}
			}

			var start = new SessionStart(repo, input, provider, documents.Directory, device(source));
			return await (request.Existing
				? AttachExistingSessionAsync(start, request.Branch, ct)
				: CreateWorktreeSessionAsync(start, source, request.Branch, request.Base, ct)).ConfigureAwait(false);
		}, ct);
	}

	private Task<CommandResult> ForkSessionAsync(
		HostSession source,
		ForkSessionRequest request,
		CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(request);
		var input = InitialSessionInput.FromText(request.Handoff);
		return GatedAsync(_sessionCatalog, async () => {
			if (await CurrentSourceAsync(source.Address, ct).ConfigureAwait(false) is not { } sourceSlot) return SourceGone();
			using var documents = _workflowDocuments.Acquire(sourceSlot.WorktreePath);
			return await CreateWorktreeSessionAsync(
					new(sourceSlot.Repo, input, sourceSlot.AgentProviderId, documents.Directory, SourceDevice(sourceSlot)),
					sourceSlot,
					request.Branch,
					"source",
					ct).ConfigureAwait(false);
		}, ct);
	}

	private Task<CommandResult> LoadSessionAsync(string? sessionId, CancellationToken ct) =>
		string.IsNullOrWhiteSpace(sessionId)
			? Task.FromResult(CommandResult.Failure("Load needs a session id."))
			: RunSlotLifecycleAsync(sessionId, NoSuchSession(sessionId), target => LoadSessionCoreAsync(target, ct), ct);

	// A session git couldn't place has no repository to run in.
	private CommandResult? LostRefusal(SessionSlot slot) => slot.Lost is { } lost
		? CommandResult.Failure($"'{NameOf(slot)}' can't find its repository, so it can't be loaded: {lost.Reason}")
		: null;

	private Task<CommandResult> LoadSessionCoreAsync(SessionSlot target, CancellationToken ct) {
		ct.ThrowIfCancellationRequested();
		if (LostRefusal(target) is { } lost) {
			return Task.FromResult(lost);
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
					$"Loaded session '{NameOf(target)}' in the background.",
					SessionAddressJson(target)));
			} catch (Exception ex) {
				result.SetResult(CommandResult.Failure(ReportSessionRestoreFailure(target, ex)));
			}
		});
		return result.Task;
	}

	private async Task<CommandResult> UnloadSessionAsync(
		HostSession? source,
		string? sessionId,
		bool stopBackgroundWork,
		CommandInvocationContext context,
		CancellationToken ct) =>
		await RunSlotLifecycleAsync(
			sessionId,
			NoSuchSession(sessionId),
			target => BackgroundWorkRefusal(target.Session, stopBackgroundWork) is { } refusal
				? Task.FromResult(refusal)
				: UnloadSessionCoreAsync(source, target, context, ct),
			ct).ConfigureAwait(false);

	private async Task<CommandResult> UnloadSessionCoreAsync(
		HostSession? source,
		SessionSlot target,
		CommandInvocationContext context,
		CancellationToken ct) {
		ct.ThrowIfCancellationRequested();
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
			await GatedAsync(
				target.Lifecycle,
				() => UnloadSlotAndNotifyAsync(target, ct),
				ct).ConfigureAwait(false);
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception ex) {
			Notify("error", $"Couldn't unload session '{NameOf(target)}': {ex.Message}");
			throw;
		}
	}

	private async Task UnloadSlotAndNotifyAsync(SessionSlot target, CancellationToken ct) {
		if (await _ui.InvokeAsync(() => UnloadSlotAsync(target), ct).ConfigureAwait(false)) {
			Notify("info", $"Session '{NameOf(target)}' was unloaded. Its worktree was kept.");
		}
	}

	private Task<CommandResult> DeleteSessionAsync(
		HostSession? source,
		string? sessionId,
		SessionDeleteConsent consent,
		bool stopBackgroundWork,
		CommandInvocationContext context,
		CancellationToken ct) =>
		RunSlotLifecycleAsync(
			sessionId,
			NoSuchSession(sessionId),
			target => {
				// The slot confirmed as keeping its folder may since have been replaced by one on a worktree.
				if (consent == SessionDeleteConsent.KeepFolder && !KeepsFolder(target)) {
					return Task.FromResult(CommandResult.Failure(
						$"'{NameOf(target)}' has changed since its delete was confirmed: deleting it now would remove its worktree. Try again."));
				}

				return BackgroundWorkRefusal(target.Session, stopBackgroundWork) is { } refusal
					? Task.FromResult(refusal)
					: DeleteSessionAfterValidationAsync(source, target, consent == SessionDeleteConsent.LoseWork, context, ct);
			},
			ct);

	// Nothing keeps this checkout's commits when it has no branch: a detached HEAD, or a checkout git can no
	// longer answer for. Removing it leaves them unreachable, so the delete costs force either way.
	private static async Task<bool> IsBranchlessAsync(string worktreePath, CancellationToken ct) {
		try {
			return await new GitService().GetCurrentBranchAsync(worktreePath, ct).ConfigureAwait(false) is null;
		} catch (GitException) {
			return true;
		}
	}

	private async Task<CommandResult> DeleteSessionAfterValidationAsync(
		HostSession? source,
		SessionSlot target,
		bool force,
		CommandInvocationContext context,
		CancellationToken ct) {
		string worktreePath = target.WorktreePath;
		string label = NameOf(target);
		bool branchless = false;
		try {
			if (target.Session is { } session
				&& await FlushSessionViewAsync(session, ct).ConfigureAwait(false) is { } flushFailure) {
				return flushFailure;
			}

			// Check what the delete would cost BEFORE tearing anything down, so a blocked delete leaves the session
			// untouched rather than unloading it as a side effect. Skip when the worktree is gone/half-removed
			// (no .git) — nothing left to lose, and git can't answer git status there. Read-only git probes, so
			// they need no UI-thread marshaling.
			// git itself refuses a locked worktree, in its own words. A folder that is kept loses nothing.
			if (!KeepsFolder(target) && IsLiveWorktree(worktreePath)) {
				branchless = await IsBranchlessAsync(worktreePath, ct).ConfigureAwait(false);
				if (branchless && !force) {
					return CommandResult.Failure(
						$"Session '{label}' has no branch keeping its commits, so deleting it would leave them unreachable. Re-run with force to delete anyway.");
				}

				if (!force && await new GitService().HasUncommittedChangesAsync(worktreePath, ct).ConfigureAwait(false)) {
					return CommandResult.Failure(
						$"Session '{label}' has uncommitted changes; deleting would discard them. Re-run with force to delete anyway.");
				}
			}
		} catch (GitException ex) {
			return CommandResult.Failure($"Couldn't delete session '{label}': {ex.Message}");
		}

		if (target.Session is { } targetSession && ReferenceEquals(targetSession, source)) {
			context.AfterReply(ct => DeleteAfterReplyAsync(target, label, force, branchless, ct));
			return CommandResult.Success();
		}

		return await DeleteAfterPreflightAsync(target, label, force, branchless, ct).ConfigureAwait(false);
	}

	private async Task DeleteAfterReplyAsync(
		SessionSlot target,
		string label,
		bool force,
		bool branchless,
		CancellationToken ct) {
		try {
			var result = await GatedAsync(
				target.Lifecycle,
				() => DeleteAfterPreflightAsync(target, label, force, branchless, ct),
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
			// A reply taken before a host edit the page had not yet applied is refused; the next one includes it.
			EditorFlushResult? result;
			do {
				result = await session.View.Feature("editor")
					.TryRequestAsync(
						"flush", WireJson.Default.EmptyPayload, WireJson.Default.EditorFlushResult,
						EmptyPayload.Value,
						ct)
					.ConfigureAwait(false);
			} while (result is not null && !HandleEditorSessionChanged(session, result.Session, result.Basis));

			return null;
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return CommandResult.Failure(
				$"Couldn't save the editor state for session '{NameOf(session)}': {ex.Message}");
		}
	}

	private async Task<CommandResult> DeleteAfterPreflightAsync(
		SessionSlot target,
		string label,
		bool force,
		bool branchless,
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

			if (KeepsFolder(target)) {
				string kept = $"Session '{label}' was deleted. Its folder {target.WorktreePath} was kept.";
				if (IsWorkspaceCheckout(target)) {
					// It stays deleted across restarts, until it is opened again.
					_sessionStore.HomeClosed = true;
					await RetireSlotAsync(target).ConfigureAwait(false);
					Notify("info", kept, $"session-deleted:{target.Id}", "Open Again", SessionCommands.OpenHomeSession, null);
				} else {
					await RetireSlotAsync(target).ConfigureAwait(false);
					Notify("info", kept);
				}

				return CommandResult.Success();
			}

			// Settle before removal: Windows can lag on releasing the unloaded children's handles, and external
			// scanners may briefly hold a lock. A short pause lets git's one-shot remove succeed instead of
			// partial-failing and orphaning the directory (git deletes its own record mid-failure, unrecoverable).
			await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
			await target.Repo.RequireWorktrees().RemoveAsync(
				target.WorktreePath,
				deleteBranch: false,
				force,
				CancellationToken.None).ConfigureAwait(false);
			await RetireSlotAsync(target).ConfigureAwait(false);
			Notify("info", branchless
				? $"Session '{label}' was deleted. Its checkout had no branch to keep."
				: $"Session '{label}' was deleted. Its branch was kept.");
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

	private Task<CommandResult> ClassifyDeleteAsync(string? sessionId, CancellationToken ct) =>
		RunSlotLifecycleAsync(sessionId, NoSuchSession(sessionId), target => ClassifyDeleteCoreAsync(target, ct), ct);

	private async Task<CommandResult> ClassifyDeleteCoreAsync(SessionSlot target, CancellationToken ct) {
		// A gone/half-removed worktree (no .git) can't be inspected and has nothing left to lose — classify clean.
		string state = "clean";
		IReadOnlyList<string> tracked = [];
		IReadOnlyList<string> untracked = [];
		bool branchless = false;
		if (KeepsFolder(target)) {
			state = "kept";
		} else if (IsLiveWorktree(target.WorktreePath)) {
			try {
				branchless = await IsBranchlessAsync(target.WorktreePath, ct).ConfigureAwait(false);
				var status = await new GitService().GetChangeStateAsync(target.WorktreePath, ct).ConfigureAwait(false);
				state = status.State switch {
					WorktreeChangeState.UntrackedOnly => "untracked",
					WorktreeChangeState.Modified => "modified",
					_ => "clean",
				};
				tracked = status.TrackedFiles;
				untracked = status.UntrackedFiles;
			} catch (GitException ex) {
				return CommandResult.Failure($"Couldn't check '{NameOf(target)}' for changes: {ex.Message}");
			}
		}

		// Name the first few changes the delete would discard; the dialog renders "…and N more" from the total.
		const int previewLimit = 5;
		string[] changed = [.. tracked.Concat(untracked).Order(StringComparer.Ordinal)];
		return CommandResult.Success(null, JsonSerializer.Serialize(
			new SessionDeletePreview(state, NameOf(target), branchless, [.. changed.Take(previewLimit)], changed.Length),
			WireJson.Default.SessionDeletePreview));
	}

	/// <summary>
	/// Drops a slot that is leaving the workspace: its agent records, its place in the catalog, and the persisted state.
	/// The slot-set mutation + rail push run on the UI thread (the caller's awaits left it), so the removal can't
	/// interleave with a concurrent switch reading the slot set.
	/// </summary>
	private async Task RetireSlotAsync(SessionSlot slot) {
		ForgetWorkflowRun(slot.WorktreePath);
		_acpSessions.ClearWorkspace(slot.WorktreePath);
		await _ui.InvokeAsync(() => {
			RemoveSlot(slot);
			PushSessionList();
			PersistSessionState();
			_workflowDocuments.Bind(slot.WorktreePath, null);
			return Task.CompletedTask;
		}, CancellationToken.None).ConfigureAwait(false);
	}

	private async Task<bool> UnloadSlotAsync(SessionSlot slot) {
		if (slot.Session is not { } session) {
			return false;
		}

		bool detached;
		try {
			await session.DisposeAsync().ConfigureAwait(false);
		} finally {
			detached = await _ui.InvokeAsync(() => {
				if (ReferenceEquals(slot.Session, session)) {
					slot.Session = null;
					_fileRoutes.Unregister(session.Incarnation);
					PushSessionList();
					PersistSessionState();
					return Task.FromResult(true);
				}

				return Task.FromResult(false);
			}, CancellationToken.None).ConfigureAwait(false);
		}
		return detached;
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

	// What a new session starts from: the repo it is created in, its first input, its provider, its documents, and its device.
	private sealed record SessionStart(
		Repo Repo, InitialSessionInput? Input, string AgentProviderId, string? WorkflowDocumentsDirectory, DeviceIdentity? Device);

	private async Task<CommandResult> CreateWorktreeSessionAsync(
		SessionStart start,
		SessionSlot? source,
		string? requestedBranch,
		string? baseSpec,
		CancellationToken ct) {
		try {
			_agentProviders.RequireAvailable(start.AgentProviderId);
		} catch (InvalidOperationException ex) {
			return CommandResult.Failure(ex.Message);
		}

		if (start.Repo.Worktrees is not { } worktrees) {
			return CommandResult.Failure(start.Repo.NotARepository);
		}

		if (string.IsNullOrWhiteSpace(requestedBranch)) {
			return CommandResult.Failure("Type a branch name to create a session.");
		}

		string branch = requestedBranch.Trim();
		// The branch name is web-supplied; reject a malformed/option-shaped name before it reaches git.
		try {
			if (!await new GitService().IsValidBranchNameAsync(start.Repo.Root, branch, ct).ConfigureAwait(false)) {
				return CommandResult.Failure($"'{branch}' isn't a valid branch name.");
			}
		} catch (GitException ex) {
			return CommandResult.Failure($"Couldn't validate the branch name: {ex.Message}");
		}

		SessionBase baseRef;
		try {
			baseRef = await ResolveBaseRefAsync(start.Repo, source, baseSpec, ct).ConfigureAwait(false);
		} catch (Exception ex) when (ex is GitException or InvalidOperationException) {
			return CommandResult.Failure($"Couldn't resolve the base ref: {ex.Message}");
		}

		WorktreeRecord record;
		try {
			record = await worktrees.CreateAsync(branch, baseRef.Ref, start.AgentProviderId, ct).ConfigureAwait(false);
		} catch (Exception ex) when (ex is InvalidOperationException or GitException) {
			return CommandResult.Failure($"Couldn't create the worktree: {ex.Message}");
		}

		if (baseRef.Parent is { } parent) {
			try {
				await new GitService().RecordParentBranchAsync(record.Path, branch, parent, ct).ConfigureAwait(false);
			} catch (GitException ex) {
				_ui.Post(() => Notify("warn", $"Couldn't record '{parent}' as the branch '{branch}' is stacked on: {ex.Message}"));
			}
		}

		return await BuildSlotAsync(start, branch, record, setUp: true, $"Created session on branch '{branch}' at {record.Path}.")
			.ConfigureAwait(false);
	}

	/// <summary>
	/// Creates a session by checking out an existing branch into a new worktree. If Weavie already has a session
	/// for that branch — or it's the workspace checkout's own branch — switches to that instead of duplicating.
	/// </summary>
	private async Task<CommandResult> AttachExistingSessionAsync(
		SessionStart start,
		string? requestedBranch,
		CancellationToken ct) {
		try {
			_agentProviders.RequireAvailable(start.AgentProviderId);
		} catch (InvalidOperationException ex) {
			return CommandResult.Failure(ex.Message);
		}

		if (start.Repo.Worktrees is not { } worktrees) {
			return CommandResult.Failure(start.Repo.NotARepository);
		}

		if (string.IsNullOrWhiteSpace(requestedBranch)) {
			return CommandResult.Failure("Pick an existing branch to check out.");
		}

		string branch = requestedBranch.Trim();
		if (!GitService.IsValidBranchName(branch)) {
			return CommandResult.Failure($"'{branch}' isn't a valid branch name.");
		}

		// A branch is checked out in one worktree at most (git refuses a second), so a session already on it is
		// switched to, and the home folder's is opened again when the branch is there and its session was deleted.
		try {
			string? checkedOutAt = await WorktreeOnBranchAsync(start.Repo, branch, ct).ConfigureAwait(false);
			if (checkedOutAt is not null && _sessions?.Find(SessionSlot.IdFor(checkedOutAt)) is { } holder) {
				return ExistingSessionInputError(start.Input) ?? await LoadExistingAsync(holder, branch, ct).ConfigureAwait(false);
			}
			if (checkedOutAt is not null && IsWorkspaceCheckout(checkedOutAt)) {
				return ExistingSessionInputError(start.Input)
					?? await OpenHomeSessionCoreAsync(start.AgentProviderId, ct).ConfigureAwait(false);
			}
		} catch (GitException ex) {
			return CommandResult.Failure($"Couldn't read where '{branch}' is checked out: {ex.Message}");
		}

		// Only run setup on a freshly-created worktree; a branch Weavie already tracks reuses its existing one.
		WorktreeRecord record;
		bool freshWorktree;
		try {
			(record, freshWorktree) = await worktrees.AttachAsync(branch, start.AgentProviderId, ct).ConfigureAwait(false);
		} catch (Exception ex) when (ex is InvalidOperationException or GitException) {
			return CommandResult.Failure($"Couldn't check out '{branch}': {ex.Message}");
		}

		// The branch may have been switched to inside a worktree a session already holds: switch to that session.
		if (!freshWorktree && _sessions?.Find(SessionSlot.IdFor(record.Path)) is { } owner) {
			if (ExistingSessionInputError(start.Input) is { } error) {
				return error;
			}
			return await LoadExistingAsync(owner, branch, ct).ConfigureAwait(false);
		}

		// Seed the first input only on this fresh-checkout path; switching to an existing session (above) must
		// never re-seed it. The Open-PR flow uses this to brief Claude on the PR it just checked out.
		// Only a freshly-created worktree is set up; a branch Weavie already tracks reuses its existing one.
		return await BuildSlotAsync(
			start with { AgentProviderId = record.AgentProviderId },
			branch,
			record,
			setUp: freshWorktree,
			$"Checked out '{branch}' at {record.Path}.").ConfigureAwait(false);
	}

	private static CommandResult? ExistingSessionInputError(InitialSessionInput? input) =>
		input is null
			? null
			: CommandResult.Failure("A prompt, images, or a workflow can't be submitted when opening a session that already exists.");

	/// <summary>
	/// Builds a <see cref="SessionSlot"/> for a worktree <paramref name="record"/>, adds it to the rail, and
	/// returns its exact address so the calling page can select it (optionally seeding its first input).
	/// </summary>
	private Task<CommandResult> BuildSlotAsync(
		SessionStart start,
		string branch,
		WorktreeRecord record,
		bool setUp,
		string successMessage) {
		var sessions = _sessions
			?? throw new InvalidOperationException("The session catalog is not initialized.");
		var result = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		_ui.Post(() => {
			SessionSlot? slot = null;
			try {
				IReadOnlyList<string> shellTerminals = [ShellTerminalId.New()];
				var created = new SessionSlot {
					Label = branch,
					Device = NewDevice(start.Device),
					Repo = start.Repo,
					WorktreePath = record.Path,
					WorkflowDocumentsDirectory = start.WorkflowDocumentsDirectory,
					AgentProviderId = start.AgentProviderId,
					EditorSession = EditorSession.Empty,
					ShellTerminals = shellTerminals,
				};
				created.Session = CreateSession(created);
				slot = created;
				slot.Session.InheritedWorkflowDocuments = start.WorkflowDocumentsDirectory;
				// The repo has a session now, so it is one of the workspace's; its setup command (e.g. `pnpm install`)
				// runs in the background, toasting "setting up… → ready/failed".
				Join(start.Repo);
				sessions.Add(slot);
				if (setUp) {
					StartWorktreeSetup(start.Repo, record.Path);
				}

				PushSessionList();
				if (start.Input?.Workflow is { } workflow) {
					slot.Session.Workflow.Start(workflow);
				} else if (start.Input is not null) {
					slot.Session.QueueInitialInput(MaterializeInitialInput(slot.Session, start.Input));
				}
				ActivateSessionRuntimeAndMessages(slot.Session);
				PersistSessionState();
				_workflowDocuments.Bind(slot.WorktreePath, slot.Session.WorkflowDocumentsDirectory);

				result.SetResult(CommandResult.Success(
					successMessage,
					CreatedSessionActivationJson(slot)));
			} catch (Exception ex) {
				result.SetException(slot is null
					? ex
					: RollbackSessionLoad(slot, slot.Session, removeSlot: true, error: ex));
			}
		});
		return result.Task;
	}

	private static bool TryDecodeInitialInput(
		string? prompt,
		IReadOnlyList<NewSessionAttachment> attachments,
		out InitialSessionInput? input,
		out string error) {
		if (!TryDecodeAttachments(attachments, out var decoded, out error)) {
			input = null;
			return false;
		}

		input = InitialSessionInput.Create(prompt, decoded);
		return true;
	}

	private static bool TryDecodeAttachments(
		IReadOnlyList<NewSessionAttachment> attachments,
		out IReadOnlyList<InitialSessionAttachment> decoded,
		out string error) {
		ArgumentNullException.ThrowIfNull(attachments);
		try {
			var ids = new HashSet<string>(StringComparer.Ordinal);
			var images = new List<InitialSessionAttachment>(attachments.Count);
			foreach (var attachment in attachments) {
				if (string.IsNullOrWhiteSpace(attachment.Id)) {
					throw new InvalidOperationException("A new-session image is missing its attachment id.");
				}
				if (!ids.Add(attachment.Id)) {
					throw new InvalidOperationException($"Attachment '{attachment.Id}' was included more than once.");
				}

				var (extension, bytes) = PastedImageMedia.Decode(attachment.Mime, attachment.DataB64);
				images.Add(new InitialSessionAttachment(attachment.Id, attachment.Mime, extension, bytes));
			}

			decoded = images;
			error = string.Empty;
			return true;
		} catch (Exception ex) when (ex is FormatException or InvalidOperationException) {
			decoded = [];
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
			Kind = AgentTurnSubmissionKind.Prompt,
			CommandName = string.Empty,
			Attachments = [.. input.Attachments.Select(attachment => new AgentInputAttachment.Image {
				Id = attachment.Id,
				Mime = attachment.Mime,
				Path = session.PastedImages.Write(attachment.Extension, attachment.Bytes),
			})],
		};

	private sealed record InitialSessionAttachment(
		string Id,
		string Mime,
		string Extension,
		byte[] Bytes);

	// A new session's first input: a prompt with images, or a workflow that takes them as its run's prompt and whose
	// first stage is the opening turn.
	private sealed record InitialSessionInput(
		string Text, IReadOnlyList<InitialSessionAttachment> Attachments, WorkflowLaunch? Workflow) {
		public static InitialSessionInput? Create(
			string? text,
			IReadOnlyList<InitialSessionAttachment> attachments) =>
			string.IsNullOrWhiteSpace(text) && attachments.Count == 0
				? null
				: new InitialSessionInput(text?.Trim() ?? string.Empty, attachments, null);

		public static InitialSessionInput ForWorkflow(WorkflowLaunch workflow) => new(string.Empty, [], workflow);

		public static WorkflowStartPrompt WorkflowPrompt(InitialSessionInput? input) => input is null
			? WorkflowStartPrompt.None
			: new(input.Text, [.. input.Attachments.Select(image => new WorkflowStartImage(image.Extension, image.Mime, image.Bytes))]);

		public static InitialSessionInput? FromText(string? text) => Create(text, []);
	}

	private Task<CommandResult> LoadExistingAsync(SessionSlot existing, string branch, CancellationToken ct) =>
		RunSlotLifecycleAsync(
			existing.Id,
			$"Session '{branch}' was deleted while opening it.",
			slot => LoadExistingCoreAsync(slot, branch),
			ct);

	private Task<CommandResult> LoadExistingCoreAsync(SessionSlot slot, string branch) {
		if (LostRefusal(slot) is { } lost) {
			return Task.FromResult(lost);
		}

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
		return JsonSerializer.Serialize(new SessionAddressData(slot.Id, address), WireJson.Default.SessionAddressData);
	}

	private static string SessionActivationJson(SessionSlot slot) =>
		JsonSerializer.Serialize(new SessionActivation(slot.Id, LiveAddress(slot), true), WireJson.Default.SessionActivation);

	private static string CreatedSessionActivationJson(SessionSlot slot) =>
		JsonSerializer.Serialize(
			new CreatedSessionActivation(slot.Id, LiveAddress(slot), true, true),
			WireJson.Default.CreatedSessionActivation);

	private static SessionAddress LiveAddress(SessionSlot slot) => LiveAddress(
		slot.Session ?? throw new InvalidOperationException("A dormant session has no live address."));

	private static SessionAddress LiveAddress(HostSession session) => session.Address;

	// Where a new session's branch starts, and the branch it is stacked on when it starts from another session's.
	private readonly record struct SessionBase(string Ref, string? Parent);

	private static async Task<SessionBase> ResolveBaseRefAsync(
		Repo repo,
		SessionSlot? source,
		string? baseSpec,
		CancellationToken ct) {
		var git = new GitService();
		if (string.Equals(baseSpec, "source", StringComparison.OrdinalIgnoreCase)) {
			if (source is null) {
				throw new InvalidOperationException("There is no source session to branch from.");
			}

			if (source.Repo != repo) {
				throw new InvalidOperationException($"The source session is in '{source.Repo.Label}', not '{repo.Label}'.");
			}

			return new(
				await git.GetHeadCommitAsync(source.WorktreePath, ct).ConfigureAwait(false),
				await git.GetCurrentBranchAsync(source.WorktreePath, ct).ConfigureAwait(false));
		}

		if (string.IsNullOrWhiteSpace(baseSpec)
			|| string.Equals(baseSpec, "main", StringComparison.OrdinalIgnoreCase)) {
			// origin's tip, by sha: the local ref is only as new as the last pull, and a single-branch clone's
			// refspec never writes origin/<branch>.
			string branch = await git.ResolveDefaultBranchAsync(repo.Root, ct).ConfigureAwait(false)
				?? throw new InvalidOperationException("This repository has no default branch.");
			if (await git.GetRemoteUrlAsync(repo.Root, "origin", ct).ConfigureAwait(false) is null) {
				return new(branch, null);
			}

			string sha = await git.RemoteBranchCommitAsync(repo.Root, "origin", branch, ct).ConfigureAwait(false)
				?? throw new InvalidOperationException($"origin has no '{branch}'.");
			await git.FetchCommitAsync(repo.Root, "origin", sha, ct).ConfigureAwait(false);
			return new(sha, null);
		}

		throw new InvalidOperationException($"Unknown session base '{baseSpec}'.");
	}


}

internal sealed record SessionStatusChanged(string Status);
internal sealed record SessionActivityChanged(string? Activity);

internal sealed record SessionAddressData(string Id, SessionAddress Address);

internal sealed record SessionActivation(string Id, SessionAddress Address, bool ActivateSession);

internal sealed record CreatedSessionActivation(string Id, SessionAddress Address, bool ActivateSession, bool CreatedSession);

internal sealed record SessionDeletePreview(string State, string Label, bool Branchless, IReadOnlyList<string> ChangedFiles, int ChangedCount);
