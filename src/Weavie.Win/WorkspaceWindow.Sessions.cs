using System.Text.Json;
using Weavie.Core;
using Weavie.Core.Commands;
using Weavie.Core.FileSystem;
using Weavie.Core.Git;
using Weavie.Core.Sessions;
using Weavie.Core.Theming;
using Weavie.Core.Worktrees;
using Weavie.Win.Hosting;

namespace Weavie.Win;

// Multi-session + worktree wiring for the workspace window: per-session command/push wiring (gated so only
// the active session drives the page), the SessionManager + WorktreeManager construction, reconcile-on-open
// (so no worktree goes unsurfaced), the session-list push that feeds the web rail, and the ISessionHost
// implementation (new / fork / close) that Claude's runCommand + the rail invoke. v1 caveats: switching
// swaps the terminals + status; the editor/LSP follow the active session's backend but the page's editor
// tabs and LSP connection don't yet re-bind on switch (documented in docs/specs/multi-session-and-worktrees.md).
internal sealed partial class WorkspaceWindow : ISessionHost {
	private readonly Dictionary<string, string> _sessionLabels = new(StringComparer.Ordinal);

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

	/// <summary>
	/// On open, reconcile the worktree registry against real git so a crash or an external removal can't
	/// leave a worktree unsurfaced; tell the user (a toast) when extra worktrees exist for this workspace.
	/// </summary>
	private async Task ReconcileWorktreesOnOpenAsync() {
		if (_worktrees is null) {
			return;
		}

		try {
			var report = await _worktrees.ReconcileAsync().ConfigureAwait(true);
			int extra = report.Statuses.Count(s => !s.IsPrimary && s.Exists);
			if (extra > 0) {
				_bridge.PostToWeb(JsonSerializer.Serialize(new {
					type = "notify",
					level = "info",
					message = $"{extra} git worktree(s) are tracked for this workspace — open the session rail to manage them.",
				}));
			}
		} catch (GitException ex) {
			Console.WriteLine($"[weavie] worktree reconcile failed: {ex.Message}");
		}
	}

	/// <summary>Pushes the session list (id, label, active, status, identity) to the page's rail.</summary>
	private void PushSessionList() {
		if (_sessions is null) {
			return;
		}

		var sessions = _sessions.Sessions.Select(s => {
			string label = SessionLabel(s);
			return new {
				id = s.Id,
				label,
				active = ReferenceEquals(_session, s),
				status = StatusName(s.Status.Status),
				hue = SessionIdentity.Hue(label),
				monogram = SessionIdentity.Monogram(label),
			};
		});
		_bridge.PostToWeb(JsonSerializer.Serialize(new { type = "session-list", sessions }));
	}

	private string SessionLabel(HostSession session) =>
		_sessionLabels.TryGetValue(session.Id, out string? label) ? label : session.Id;

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

	/// <summary>Creates + wires a new <see cref="HostSession"/> rooted at <paramref name="cwd"/> labeled <paramref name="label"/>.</summary>
	private HostSession CreateSession(string cwd, string label) {
		var session = new HostSession(
			_bridge, _settings, _layout, cwd, WeaviePaths.WorkspaceScratchDir(Id), _pageOrigin,
			Guid.NewGuid().ToString("n")[..8],
			_app.CommandRegistry, _app.Keybindings, _app.ThemeOverrides);
		_sessionLabels[session.Id] = label;
		WireSession(session);
		_sessions?.Add(session, activate: false);
		return session;
	}

	/// <summary>
	/// Binds the page to <paramref name="session"/>: mutes the previous session's terminals (they keep
	/// running in the background), unmutes the new one's, and resets the page's xterms so they re-emit
	/// term-ready and the new session's terminals start/resize. Then re-pushes status + the rail.
	/// </summary>
	private void SwitchToSession(HostSession session) {
		var previous = _session;
		if (previous is not null && !ReferenceEquals(previous, session)) {
			previous.Claude.OutputActive = false;
			previous.Shell.OutputActive = false;
		}

		_session = session;
		_sessions?.SetActive(session);
		session.Claude.OutputActive = true;
		session.Shell.OutputActive = true;

		// Clear the page's xterms; the page re-emits term-ready, which routes to the now-active session's
		// terminals (Start spawns a freshly-created session's PTYs; a resize repaints an existing TUI).
		_bridge.PostToWeb("{\"type\":\"term-reset\",\"session\":\"claude\"}");
		_bridge.PostToWeb("{\"type\":\"term-reset\",\"session\":\"shell\"}");
		PostSessionStatus(session.Status.Status);
		PushSessionList();
	}

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
	public Task<CommandResult> CloseSessionAsync(string? sessionId, CancellationToken ct) {
		var target = string.IsNullOrWhiteSpace(sessionId) ? _session : _sessions?.Find(sessionId);
		if (target is null) {
			return Task.FromResult(CommandResult.Failure("No such session."));
		}

		if (ReferenceEquals(target, _primarySession)) {
			return Task.FromResult(CommandResult.Failure("The primary session can't be closed; close the window instead."));
		}

		var result = new TaskCompletionSource<CommandResult>();
		RunOnUi(async () => {
			try {
				if (ReferenceEquals(_session, target) && _primarySession is not null) {
					SwitchToSession(_primarySession);
				}

				_sessions?.Remove(target);
				_sessionLabels.Remove(target.Id);
				await target.DisposeAsync().ConfigureAwait(true);
				PushSessionList();
				result.SetResult(CommandResult.Success("Closed the session (its worktree is kept and can be reopened)."));
			} catch (Exception ex) {
				result.SetException(ex);
			}
		});
		return result.Task;
	}

	private async Task<CommandResult> CreateWorktreeSessionAsync(string? requestedBranch, string? baseSpec, string? prompt, CancellationToken ct) {
		if (_worktrees is null) {
			return CommandResult.Failure("This workspace isn't a git repository, so worktree-backed sessions aren't available.");
		}

		string branch = string.IsNullOrWhiteSpace(requestedBranch) ? DeriveBranchName(prompt) : requestedBranch.Trim();
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
				var session = CreateSession(record.Path, branch);
				SwitchToSession(session);
				if (!string.IsNullOrWhiteSpace(prompt)) {
					SeedFirstPrompt(session, prompt);
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

	private string DeriveBranchName(string? prompt) {
		string slug = "session";
		if (!string.IsNullOrWhiteSpace(prompt)) {
			char[] chars = [.. prompt.Trim().ToLowerInvariant().Take(40).Select(c => char.IsLetterOrDigit(c) ? c : '-')];
			slug = new string(chars).Trim('-');
			if (slug.Length == 0) {
				slug = "session";
			}
		}

		// Ensure uniqueness against existing session labels.
		string candidate = slug;
		int n = 2;
		while (_sessionLabels.Values.Contains(candidate, StringComparer.Ordinal)) {
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
