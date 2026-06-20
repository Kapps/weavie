using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Weavie.Core;
using Weavie.Core.FileSystem;
using Weavie.Core.Git;
using Weavie.Core.Workspaces;
using Weavie.Core.Worktrees;

namespace Weavie.Runner;

/// <summary>
/// The runner's manager: the long-lived factory + supervisor that creates remote sessions on demand and
/// tears them down. Each create makes a real git worktree (reusing Core's <see cref="WorktreeManager"/>, so
/// nothing leaks) and spawns a supervised <c>Weavie.Headless</c> worker rooted at it. It is not itself a
/// session backend — it only mints them. See docs/specs/remote-sessions.md.
/// </summary>
public sealed class SessionManager : IAsyncDisposable {
	private readonly ConcurrentDictionary<string, RemoteSession> _sessions = new();
	private readonly WorktreeManager _worktrees;
	private readonly HeadlessLauncher _launcher;

	private SessionManager(WorktreeManager worktrees, HeadlessLauncher launcher) {
		_worktrees = worktrees;
		_launcher = launcher;
	}

	/// <summary>
	/// Builds a manager for the repository at <paramref name="options"/>.<see cref="RunnerOptions.WorkspaceRoot"/>,
	/// or returns <c>null</c> when that root is not a git repository (worktree-backed sessions need git).
	/// </summary>
	public static async Task<SessionManager?> CreateAsync(RunnerOptions options, Action<string> log) {
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(log);

		var git = new GitService();
		try {
			if (!await git.IsRepositoryAsync(options.WorkspaceRoot).ConfigureAwait(false)) {
				return null;
			}
		} catch (GitException) {
			return null;
		}

		var id = WorkspaceId.ForPath(options.WorkspaceRoot);
		var registry = new WorktreeRegistry(new LocalFileSystem(), WeaviePaths.WorkspaceWorktreesFile(id));
		registry.Log += line => log($"[worktrees] {line}");
		var manager = new WorktreeManager(
			git, registry, options.WorkspaceRoot, WeaviePaths.WorkspaceWorktreesDir(id), NullWorktreeProvisioner.Instance);

		var launcher = new HeadlessLauncher(options, entry => log($"[{entry.Name}] {entry.Level.ToString().ToLowerInvariant()}: {entry.Message}"));
		return new SessionManager(manager, launcher);
	}

	/// <summary>A snapshot of the live sessions, oldest first.</summary>
	public IReadOnlyList<RemoteSession> Sessions => _sessions.Values.OrderBy(s => s.CreatedAtUtc).ToList();

	/// <summary>Finds a session by id, or <c>null</c>.</summary>
	public RemoteSession? Find(string id) => _sessions.GetValueOrDefault(id);

	/// <summary>
	/// Creates a new session: a fresh worktree on <paramref name="branch"/> (auto-generated when omitted)
	/// started from <paramref name="baseRef"/> (<c>"head"</c> → <c>HEAD</c>, else the literal ref), and a
	/// supervised headless worker rooted at it. Throws <see cref="InvalidOperationException"/> when the branch
	/// already exists (so the caller can surface a clean 409).
	/// </summary>
	public async Task<RemoteSession> CreateAsync(string? branch, string? baseRef, CancellationToken ct = default) {
		string resolvedBranch = string.IsNullOrWhiteSpace(branch) ? GenerateBranch() : branch.Trim();
		string resolvedBase = string.IsNullOrWhiteSpace(baseRef) || baseRef == "head" ? "HEAD" : baseRef.Trim();

		var record = await _worktrees.CreateAsync(resolvedBranch, resolvedBase, ct).ConfigureAwait(false);

		var session = new RemoteSession {
			Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(),
			Branch = resolvedBranch,
			WorktreePath = record.Path,
			Port = AllocatePort(),
			Token = RunnerOptions.NewToken(),
		};
		session.Supervisor = _launcher.BuildSupervisor(session);
		_sessions[session.Id] = session;
		session.Supervisor.Start();
		return session;
	}

	/// <summary>
	/// Stops a session's worker and (best-effort) removes its worktree. No-op when the id is unknown. Returns
	/// whether a session was found and torn down.
	/// </summary>
	public async Task<bool> DestroyAsync(string id, CancellationToken ct = default) {
		if (!_sessions.TryRemove(id, out var session)) {
			return false;
		}

		session.Supervisor?.Dispose();
		try {
			await _worktrees.RemoveAsync(session.WorktreePath, deleteBranch: false, force: true, ct).ConfigureAwait(false);
		} catch (Exception ex) when (ex is GitException or WorktreeDirtyException or IOException) {
			// The worktree couldn't be removed (e.g. git is mid-operation); the registry stays reconciled on
			// the next list, so this never leaks silently.
		}

		return true;
	}

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		foreach (var session in _sessions.Values) {
			session.Supervisor?.Dispose();
		}

		_sessions.Clear();
		await ValueTask.CompletedTask.ConfigureAwait(false);
	}

	private static string GenerateBranch() {
		string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
		string suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant();
		return $"session-{stamp}-{suffix}";
	}

	/// <summary>Grabs a free TCP port by binding to port 0 and releasing it. Inherently racy, fine here.</summary>
	private static int AllocatePort() {
		var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		try {
			return ((IPEndPoint)listener.LocalEndpoint).Port;
		} finally {
			listener.Stop();
		}
	}
}
