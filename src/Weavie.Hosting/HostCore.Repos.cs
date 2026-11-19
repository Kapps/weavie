using System.Collections.Concurrent;
using Weavie.Core;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Corrections;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Git;
using Weavie.Core.Suggestions;
using Weavie.Core.Workspaces;
using Weavie.Core.Worktrees;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

// The repos this workspace's sessions live in. A repo joins with its first session and leaves with its last; its
// on-disk state is keyed by its own root, never by the workspace. See docs/specs/multi-repo-workspaces.md.
public sealed partial class HostCore {
	private readonly ConcurrentDictionary<WorkspaceId, Repo> _repos = new();
	private string? _homeMainWorktree;

	/// <summary>The home folder's repo: where a session started from no session is created.</summary>
	private Repo Home {
		get => field ?? throw new InvalidOperationException("The workspace has not started.");
		set;
	}

	/// <summary>What a page shows of a repo, keyed by its root: sent whole at hello, then per value as each changes.</summary>
	internal sealed record RepoState(
		string Repo, string TestProfile, IReadOnlyList<SuggestionWire> Suggestions, IReadOnlyList<string> RecentFiles);

	private RepoState[] RepoStates() => [.. _repos.Values.Select(repo => new RepoState(
		repo.Root, ResolvedTestProfile(repo), SuggestionWires(repo.Suggestions.Current), RecentFiles(repo)))];

	/// <summary>A repo a session can be started in, with the name that tells it from the others.</summary>
	internal sealed record KnownRepo(string Repo, string Label);

	// The home folder's, then by name every repo the workspace has had sessions in that is still on disk.
	private KnownRepo[] KnownRepos() {
		string[] named = RepoRoots();
		return [.. _sessionStore.Repos.Where(Directory.Exists).Append(Home.Root).Distinct(PathIdentity.Comparer)
			.Select(root => new KnownRepo(root, RepoLabel(root, named)))
			.OrderByDescending(repo => repo.Repo == Home.Root).ThenBy(repo => repo.Label, StringComparer.OrdinalIgnoreCase)];
	}

	// Every repo a name has to tell apart: the home folder's, the remembered ones, and those of the sessions.
	private string[] RepoRoots() => [.. (_sessions?.Slots ?? []).Select(slot => slot.Repo.Root)
		.Concat(_sessionStore.Repos).Append(Home.Root).Distinct(PathIdentity.Comparer)];

	private void PublishKnownRepos() =>
		_messages.Host.Feature("repos").Publish("known", WireJson.Default.KnownRepoArray, KnownRepos());

	/// <summary>Stops offering <paramref name="repo"/> for new sessions; a repo with a session here is kept.</summary>
	private Task<CommandResult> ForgetRepoAsync(string? repo, CancellationToken ct) => GatedAsync(_sessionCatalog, () => {
		if (string.IsNullOrWhiteSpace(repo)) {
			return Task.FromResult(CommandResult.Failure("Forgetting a repo needs its 'repo' path."));
		}

		string root = RepoPath(repo, null);
		if (_repos.ContainsKey(WorkspaceId.ForPath(root))) {
			return Task.FromResult(CommandResult.Failure($"'{root}' has a session in this workspace, or is its home folder."));
		}
		// Forgotten, saved and published in one step. The repos left are named without it, on the chips as in the list.
		return _ui.InvokeAsync(() => {
			if (!_sessionStore.ForgetRepo(root)) {
				return Task.FromResult(CommandResult.Failure($"'{root}' isn't a repo this workspace remembers."));
			}

			try {
				PersistSessionState();
			} catch {
				// Still remembered on disk, so still remembered here, and forgetting it can be tried again.
				_sessionStore.RememberRepo(root);
				throw;
			}

			PublishKnownRepos();
			PushSessionList();
			return Task.FromResult(CommandResult.Success($"Forgot '{root}'."));
		}, ct);
	}, ct);

	/// <summary>The repos a settings change reaches: the one whose overlay changed, or all for a user-level change.</summary>
	private IEnumerable<Repo> ReposAffectedBy(SettingChange change) =>
		_repos.Values.Where(repo => change.WorkspaceRoot is null || WorkspaceId.ForPath(change.WorkspaceRoot) == repo.Id);

	private Repo OpenRepo(string root, GitService git, bool isRepository) {
		var id = WorkspaceId.ForPath(root);
		var fileSystem = new LocalFileSystem();
		// Backs the repo's settings (worktree.setupCommand, test.profile) from its out-of-repo overlay.
		_settings.RegisterWorkspace(root);
		var provisioner = new ShellWorktreeProvisioner(
			() => _settings.RequireString(CoreSettings.WorktreeSetupCommand, root),
			() => _settings.RequireString(CoreSettings.WorktreeTeardownCommand, root));
		provisioner.Starting += OnWorktreeCommandStarting;
		provisioner.Finished += OnWorktreeCommandFinished;
		WorktreeManager? worktrees = null;
		if (isRepository) {
			var registry = new WorktreeRegistry(fileSystem, WeaviePaths.WorkspaceWorktreesFile(id));
			registry.Log += line => Console.WriteLine($"[worktrees] {line}");
			worktrees = new WorktreeManager(git, registry, root, WeaviePaths.WorkspaceWorktreesDir(id), provisioner);
			worktrees.Log += line => Console.WriteLine($"[worktree] {line}");
		}

		var corrections = new CorrectionCorpus(fileSystem, WeaviePaths.WorkspaceCorrectionsFile(id));
		corrections.Log += Log;
		var recentFiles = new RecentFilesStore(fileSystem, WeaviePaths.WorkspaceRecentFilesFile(id));
		recentFiles.Log += Log;
		var dismissals = new SuggestionDismissals(fileSystem, WeaviePaths.WorkspaceSuggestionsFile(id));
		dismissals.Log += Log;
		var autoConfig = new WorkspaceAutoConfig(_settings, root);
		var suggestions = new SuggestionService(
			_suggestionRegistry, _settings, fileSystem, root, dismissals, TimeSpan.FromMilliseconds(500),
			active => PushSuggestions(root, active), () => RunAutoConfigProbe(root, autoConfig, fileSystem),
			() => new SuggestionSignals(corrections.Count, _decisionHistory.Summary()));
		// Its count gates the corrections.learn card.
		corrections.Changed += suggestions.Evaluate;
		return new Repo {
			Root = root,
			Worktrees = worktrees,
			Provisioner = provisioner,
			Corrections = corrections,
			RecentFiles = recentFiles,
			Suggestions = suggestions,
		};
	}

	/// <summary>The repo containing <paramref name="path"/>: one of the workspace's, or one opened to join it.</summary>
	private async Task<Repo> RepoForAsync(string path, CancellationToken ct) =>
		await RepoAtAsync(await RepoRootAsync(path, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

	/// <summary>The repo whose root is <paramref name="root"/>: one of the workspace's, or one opened to join it.</summary>
	private async Task<Repo> RepoAtAsync(string root, CancellationToken ct) {
		if (_repos.TryGetValue(WorkspaceId.ForPath(root), out var known)) {
			return known;
		}

		var repo = OpenRepo(root, new GitService(), isRepository: true);
		await repo.RequireWorktrees().ReconcileAsync(ct).ConfigureAwait(false);
		return repo;
	}

	/// <summary>Makes <paramref name="repo"/> one of the workspace's: its state reaches the page and its auto-config runs.</summary>
	private void Join(Repo repo) {
		if (_repos.TryAdd(repo.Id, repo)) {
			PushTestProfile(repo);
			PushRecentFiles(repo);
			repo.Suggestions.Start();
			// A plain folder holds no worktree session, so it is never offered for a new one.
			if (repo.Worktrees is not null && _sessionStore.RememberRepo(repo.Root)) {
				PublishKnownRepos();
			}
		}
	}

	/// <summary>Removes <paramref name="slot"/> from the catalog; a repo other than the home folder's leaves with its last session.</summary>
	private void RemoveSlot(SessionSlot slot) {
		_sessions?.Remove(slot);
		if (slot.Repo != Home && _sessions?.Slots.Any(other => other.Repo == slot.Repo) != true) {
			_repos.TryRemove(new(slot.Repo.Id, slot.Repo));
		}
	}

	/// <summary>The root of the repository whose folder, or worktree, <paramref name="path"/> is: the home folder for the home repo.</summary>
	private async Task<string> RepoRootAsync(string path, CancellationToken ct) {
		if (_repos.TryGetValue(WorkspaceId.ForPath(path), out var known)) {
			return known.Root;
		}

		var git = new GitService();
		var (toplevel, root) = await LocateAsync(git, path, ct).ConfigureAwait(false);
		// A folder inside a repo doesn't name it: a typed "infra" must not mean the repo that has an infra/ folder.
		if (!PathIdentity.Equals(toplevel, path)) {
			throw new GitException($"'{path}' is a folder inside the repository at '{toplevel}'; name that folder.");
		}

		return Home.Worktrees is not null && PathIdentity.Equals(root, await HomeMainWorktreeAsync(git, ct).ConfigureAwait(false))
			? Home.Root
			: root;
	}

	private async Task<string> HomeMainWorktreeAsync(GitService git, CancellationToken ct) =>
		_homeMainWorktree ??= (await LocateAsync(git, Home.Root, ct).ConfigureAwait(false)).Main;

	// Where a folder sits in its repository: its own checkout's top folder, and the main worktree git lists first.
	private static async Task<(string Toplevel, string Main)> LocateAsync(GitService git, string path, CancellationToken ct) {
		if (!Directory.Exists(path)) {
			throw new GitException($"'{path}' doesn't exist.");
		}

		string toplevel = await git.FindToplevelAsync(path, ct).ConfigureAwait(false)
			?? throw new GitException($"'{path}' isn't in a git repository.");
		return (toplevel, (await git.ListWorktreesAsync(path, ct).ConfigureAwait(false))[0].Path);
	}

	/// <summary>Runs the repo's setup command in the background, once its auto-config has written one.</summary>
	private void StartWorktreeSetup(Repo repo, string worktreePath) => _ = Task.Run(async () => {
		try {
			await repo.Suggestions.Probed.ConfigureAwait(false);
			// Not tied to the create command's lifetime — a returning command must not cancel the setup.
			await repo.Provisioner.RunSetupAsync(worktreePath, CancellationToken.None).ConfigureAwait(false);
		} catch (Exception ex) {
			Console.WriteLine($"[weavie] worktree setup command failed to run: {ex}");
			_ui.Post(() => Notify(
				"warn", $"Worktree setup for '{WorktreeLabel(worktreePath)}' couldn't run: {ex.Message}"));
		}
	});

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
}
