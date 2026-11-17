using System.Collections.Concurrent;
using Weavie.Core;
using Weavie.Core.Configuration;
using Weavie.Core.Corrections;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Git;
using Weavie.Core.Suggestions;
using Weavie.Core.Workspaces;
using Weavie.Core.Worktrees;

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
	private async Task<Repo> RepoForAsync(string path, CancellationToken ct) {
		string root = await RepoRootAsync(path, ct).ConfigureAwait(false);
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
		}
	}

	/// <summary>Removes <paramref name="slot"/> from the catalog; a repo other than the home folder's leaves with its last session.</summary>
	private void RemoveSlot(SessionSlot slot) {
		_sessions?.Remove(slot);
		if (slot.Repo != Home && _sessions?.Slots.Any(other => other.Repo == slot.Repo) != true) {
			_repos.TryRemove(slot.Repo.Id, out _);
		}
	}

	/// <summary>The root of the repository containing <paramref name="path"/>: the home folder for the home repo.</summary>
	private async Task<string> RepoRootAsync(string path, CancellationToken ct) {
		if (_repos.TryGetValue(WorkspaceId.ForPath(path), out var known)) {
			return known.Root;
		}

		var git = new GitService();
		string root = await MainWorktreeAsync(git, path, ct).ConfigureAwait(false);
		return Home.Worktrees is not null
			&& PathIdentity.Equals(root, _homeMainWorktree ??= await MainWorktreeAsync(git, Home.Root, ct).ConfigureAwait(false))
				? Home.Root
				: root;
	}

	// git lists a repository's main worktree first, asked from any of its worktrees.
	private static async Task<string> MainWorktreeAsync(GitService git, string path, CancellationToken ct) =>
		Directory.Exists(path)
			? (await git.ListWorktreesAsync(path, ct).ConfigureAwait(false))[0].Path
			: throw new GitException($"'{path}' doesn't exist.");

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
