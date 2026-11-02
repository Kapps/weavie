using Weavie.Core.FileSystem;
using Weavie.Core.Git;

namespace Weavie.Core.Workspaces;

/// <summary>An authoritative workspace snapshot and the flat directories needed to observe its files.</summary>
/// <param name="IsRepository">Whether Git supplied the snapshot.</param>
/// <param name="Files">Canonical absolute paths for tracked and untracked, non-ignored files.</param>
/// <param name="Directories">Canonical absolute file ancestors and non-ignored directories, including empty ones.</param>
public sealed record WorkspaceInventorySnapshot(
	bool IsRepository,
	IReadOnlyList<string> Files,
	IReadOnlyList<string> Directories);

internal readonly record struct WorkspaceFileMove(string OldPath, string NewPath);

/// <summary>
/// Loads one workspace's files and directories from Git, including empty non-ignored directories.
/// Refreshes are serialized so every consumer sees a complete snapshot.
/// </summary>
public sealed partial class WorkspaceInventory {
	private readonly Func<CancellationToken, Task<IReadOnlyList<string>?>> _load;
	private readonly Func<IReadOnlyCollection<string>, IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlyList<string>?>> _loadScope;
	private readonly SemaphoreSlim _refreshGate = new(1, 1);
	private readonly Lock _knownFilesLock = new();
	private readonly HashSet<string> _knownNonRepositoryFiles = new(PathIdentity.Comparer);
	private readonly HashSet<string> _knownNonRepositoryDirectories = new(PathIdentity.Comparer);
	private bool? _isRepository;
	private WorkspaceInventorySnapshot? _lastSnapshot;

	/// <summary>Creates a Git-backed inventory rooted at <paramref name="root"/>.</summary>
	public WorkspaceInventory(string root) : this(
		root,
		ct => new GitService().ListWorkspacePathsAsync(root, ct),
		(known, discovered, ct) => new GitService().ListWorkspacePathsAsync(root, known, discovered, ct)) { }

	// The scoped load filters the full one, so a test supplies one listing that both refresh kinds agree on.
	internal WorkspaceInventory(
		string root,
		Func<CancellationToken, Task<IReadOnlyList<string>?>> load) : this(root, load, async (known, discovered, ct) => {
			var paths = await load(ct).ConfigureAwait(false);
			return paths?.Where(path => known.Concat(discovered).Any(scoped => PathBoundary.Contains(
				scoped, WorkspacePaths.CanonicalFsPath(PathIdentity.Normalize(path, root)), PathIdentity.Comparison))).ToList();
		}) { }

	internal WorkspaceInventory(
		string root,
		Func<CancellationToken, Task<IReadOnlyList<string>?>> load,
		Func<IReadOnlyCollection<string>, IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlyList<string>?>> loadScope) {
		ArgumentException.ThrowIfNullOrEmpty(root);
		ArgumentNullException.ThrowIfNull(load);
		ArgumentNullException.ThrowIfNull(loadScope);
		Root = WorkspacePaths.CanonicalFsPath(PathIdentity.Normalize(root));
		_load = load;
		_loadScope = loadScope;
	}

	/// <summary>The canonical absolute workspace root.</summary>
	public string Root { get; }

	/// <summary>Raised when a non-Git consumer supplies a newer already-known file set.</summary>
	public event Action? Changed;

	/// <summary>
	/// The snapshot a refresh most recently computed, or <c>null</c> before the first refresh. It is current only
	/// while a watcher keeps it so; read it through that watcher rather than directly.
	/// </summary>
	public WorkspaceInventorySnapshot? LastSnapshot => Volatile.Read(ref _lastSnapshot);

	/// <summary>
	/// Reloads the authoritative Git inventory. A non-repository snapshot contains paths already supplied by
	/// navigation; Git execution failures throw rather than silently switching to a filesystem walk.
	/// </summary>
	public async Task<WorkspaceInventorySnapshot> RefreshAsync(CancellationToken ct = default) {
		await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
		try {
			return await ReloadAsync(ct).ConfigureAwait(false);
		} finally {
			_refreshGate.Release();
		}
	}

	private async Task<WorkspaceInventorySnapshot> ReloadAsync(CancellationToken ct) {
		var paths = _isRepository is false
			? null
			: await _load(ct).ConfigureAwait(false);
		WorkspaceInventorySnapshot snapshot;
		if (paths is not null) {
			_isRepository = true;
			snapshot = BuildSnapshot(isRepository: true,
				[.. paths.Where(path => !path.EndsWith('/'))],
				[.. paths.Where(path => path.EndsWith('/'))]);
		} else {
			_isRepository = false;
			lock (_knownFilesLock) {
				snapshot = BuildSnapshot(
					isRepository: false,
					[.. _knownNonRepositoryFiles],
					[.. _knownNonRepositoryDirectories]);
			}
		}

		Volatile.Write(ref _lastSnapshot, snapshot);
		return snapshot;
	}

	/// <summary>
	/// Re-lists only the entries at or beneath <paramref name="paths"/> (absolute, inside <see cref="Root"/>) and
	/// keeps the rest of the previous snapshot; the result matches a full refresh for those paths. Returns the
	/// previous snapshot instance when nothing changed, and refreshes fully before a repository snapshot exists.
	/// Pass <see cref="CoveringScope"/>'s result to list a burst of related paths as one query.
	/// </summary>
	public async Task<WorkspaceInventorySnapshot> RefreshAsync(IReadOnlyCollection<string> paths, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(paths);
		var scope = paths.Select(path => WorkspacePaths.CanonicalFsPath(PathIdentity.Normalize(path))).ToHashSet(PathIdentity.CanonicalComparer);
		if (LastSnapshot is not { IsRepository: true } || scope.Contains(Root)) {
			return await RefreshAsync(ct).ConfigureAwait(false);
		}

		await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
		try {
			var previous = LastSnapshot!;
			var files = previous.Files.ToHashSet(PathIdentity.CanonicalComparer);
			var directories = previous.Directories.ToHashSet(PathIdentity.CanonicalComparer);
			// Every known entry beneath a scope path implies that path is known, so unknown scope paths drop nothing.
			// Known paths are always listed: an ignored directory can still hold tracked files.
			var discovered = scope.Where(path => !files.Contains(path) && !directories.Contains(path)).ToList();
			scope.ExceptWith(discovered);
			var listed = await _loadScope(scope, discovered, ct).ConfigureAwait(false);
			if (listed is null) {
				return await ReloadAsync(ct).ConfigureAwait(false);
			}

			if (listed.Count == 0 && scope.Count == 0) {
				return previous;
			}

			files.RemoveWhere(path => IsWithin(scope, path));
			directories.RemoveWhere(path => IsWithin(scope, path));
			var additions = BuildSnapshot(isRepository: true,
				[.. listed.Where(path => !path.EndsWith('/'))],
				[.. listed.Where(path => path.EndsWith('/'))]);
			files.UnionWith(additions.Files);
			directories.UnionWith(additions.Directories);
			var snapshot = new WorkspaceInventorySnapshot(true, Sorted(files), Sorted(directories));
			Volatile.Write(ref _lastSnapshot, snapshot);
			return snapshot;
		} finally {
			_refreshGate.Release();
		}
	}

	/// <summary>
	/// Replaces each path with its highest ancestor the current snapshot doesn't know, so a burst inside one new or
	/// ignored directory (an install's node_modules) refreshes as one query, and a re-list after watching that new
	/// directory covers all of it.
	/// </summary>
	public IReadOnlyCollection<string> CoveringScope(IReadOnlyCollection<string> paths) {
		ArgumentNullException.ThrowIfNull(paths);
		if (LastSnapshot is not { } snapshot) return paths;
		var directories = snapshot.Directories.ToHashSet(PathIdentity.CanonicalComparer);
		return paths
			.Select(path => HighestUnknownAncestor(directories, WorkspacePaths.CanonicalFsPath(PathIdentity.Normalize(path))))
			.ToHashSet(PathIdentity.CanonicalComparer);
	}

	private static string HighestUnknownAncestor(HashSet<string> directories, string path) {
		for (string? parent = Path.GetDirectoryName(path);
			parent is not null && !directories.Contains(parent);
			parent = Path.GetDirectoryName(parent)) {
			path = parent;
		}

		return path;
	}

	private bool IsWithin(HashSet<string> scope, string path) {
		for (string? current = path;
			current is not null && !PathIdentity.CanonicalComparer.Equals(current, Root);
			current = Path.GetDirectoryName(current)) {
			if (scope.Contains(current)) return true;
		}

		return false;
	}

	private static List<string> Sorted(HashSet<string> paths) {
		var sorted = paths.ToList();
		sorted.Sort(PathIdentity.CanonicalComparer);
		return sorted;
	}

	internal WorkspaceInventorySnapshot BuildSnapshot(
		bool isRepository,
		IReadOnlyList<string> relativeFiles,
		IReadOnlyList<string> relativeDirectories) {
		var files = new HashSet<string>(PathIdentity.CanonicalComparer);
		var directories = new HashSet<string>(PathIdentity.CanonicalComparer) { Root };
		string rootPrefix = Root.EndsWith(Path.DirectorySeparatorChar)
			? Root
			: Root + Path.DirectorySeparatorChar;

		foreach (string relative in relativeFiles) {
			string fullPath = WorkspacePaths.CanonicalFsPath(PathIdentity.Normalize(relative, Root));
			if (!fullPath.StartsWith(rootPrefix, PathIdentity.Comparison)) {
				throw new GitException($"Git returned a path outside the workspace: {relative}");
			}

			files.Add(fullPath);
			for (string? directory = Path.GetDirectoryName(fullPath);
				directory is not null;
				directory = Path.GetDirectoryName(directory)) {
				directory = WorkspacePaths.CanonicalFsPath(directory);
				if (!directories.Add(directory) || PathIdentity.CanonicalComparer.Equals(directory, Root)) {
					break;
				}
			}
		}

		foreach (string relative in relativeDirectories) {
			string fullPath = WorkspacePaths.CanonicalFsPath(PathIdentity.Normalize(relative, Root));
			if (!PathIdentity.CanonicalComparer.Equals(fullPath, Root) && !fullPath.StartsWith(rootPrefix, PathIdentity.Comparison)) {
				throw new GitException($"Workspace inventory returned a path outside the workspace: {relative}");
			}

			directories.Add(fullPath);
		}

		var sortedFiles = files.ToList();
		sortedFiles.Sort(PathIdentity.CanonicalComparer);
		var sortedDirectories = directories.ToList();
		sortedDirectories.Sort(PathIdentity.CanonicalComparer);
		return new WorkspaceInventorySnapshot(isRepository, sortedFiles, sortedDirectories);
	}

}
