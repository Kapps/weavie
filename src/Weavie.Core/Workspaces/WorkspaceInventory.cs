using System.Collections.Immutable;
using Weavie.Core.FileSystem;
using Weavie.Core.Git;

namespace Weavie.Core.Workspaces;

/// <summary>An authoritative workspace snapshot and the flat directories needed to observe its files.</summary>
/// <param name="Revision">Counts the changes that produced this snapshot; each change is exactly one more.</param>
/// <param name="Files">Canonical absolute paths for the workspace's files, none of them ignored.</param>
/// <param name="Directories">Canonical absolute file ancestors and non-ignored directories, including empty ones.</param>
public sealed record WorkspaceInventorySnapshot(
	long Revision,
	ImmutableSortedSet<string> Files,
	ImmutableSortedSet<string> Directories);

/// <summary>
/// What one refresh changed and the snapshot it produced: the previous snapshot instance when nothing changed.
/// </summary>
public sealed record WorkspaceInventoryChange(
	WorkspaceInventorySnapshot Snapshot,
	IReadOnlyCollection<string> CreatedFiles,
	IReadOnlyCollection<string> DeletedFiles,
	IReadOnlyCollection<string> AddedDirectories,
	IReadOnlyCollection<string> RemovedDirectories) {
	/// <summary>Whether the refresh found the workspace as the previous snapshot had it.</summary>
	public bool IsEmpty => CreatedFiles.Count + DeletedFiles.Count + AddedDirectories.Count + RemovedDirectories.Count == 0;
}

/// <summary>
/// Loads one workspace's files and directories from its <see cref="WorkspaceListing"/>, including empty
/// non-ignored directories. Refreshes are serialized so every consumer sees a complete snapshot.
/// </summary>
public sealed class WorkspaceInventory {
	private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _load;
	private readonly Func<IReadOnlyCollection<string>, IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlyList<string>?>> _loadScope;
	private readonly SemaphoreSlim _refreshGate = new(1, 1);
	// A filesystem root already ends in its separator; every other root gains one.
	private readonly string _rootPrefix;
	private WorkspaceInventorySnapshot? _lastSnapshot;

	/// <summary>Creates an inventory of the repository or plain folder at <paramref name="root"/>.</summary>
	public WorkspaceInventory(string root) : this(root, new WorkspaceListing(Canonical(root))) { }

	private WorkspaceInventory(string root, WorkspaceListing listing) : this(root, listing.ListAsync, listing.ListAsync) { }

	// The scoped load filters the full one, so a test supplies one listing that both refresh kinds agree on.
	internal WorkspaceInventory(
		string root,
		Func<CancellationToken, Task<IReadOnlyList<string>>> load) : this(root, load, async (known, discovered, ct) => {
			var paths = await load(ct).ConfigureAwait(false);
			return paths.Where(path => known.Concat(discovered).Any(scoped => PathBoundary.Contains(
				scoped, WorkspacePaths.CanonicalFsPath(PathIdentity.Normalize(path, root)), PathIdentity.Comparison))).ToList();
		}) { }

	internal WorkspaceInventory(
		string root,
		Func<CancellationToken, Task<IReadOnlyList<string>>> load,
		Func<IReadOnlyCollection<string>, IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlyList<string>?>> loadScope) {
		ArgumentException.ThrowIfNullOrEmpty(root);
		ArgumentNullException.ThrowIfNull(load);
		ArgumentNullException.ThrowIfNull(loadScope);
		Root = Canonical(root);
		_rootPrefix = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
		_load = load;
		_loadScope = loadScope;
	}

	private static string Canonical(string root) => WorkspacePaths.CanonicalFsPath(PathIdentity.Normalize(root));

	/// <summary>The canonical absolute workspace root.</summary>
	public string Root { get; }

	/// <summary>The <c>/</c>-separated path below the root of a file or directory this inventory lists.</summary>
	public string RelativePath(string path) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		return path[_rootPrefix.Length..].Replace(Path.DirectorySeparatorChar, '/');
	}

	/// <summary>Raised once per non-empty change, in revision order, before the refresh that made it returns.</summary>
	public event Action<WorkspaceInventoryChange>? Changed;

	/// <summary>
	/// The snapshot a refresh most recently computed, or <c>null</c> before the first refresh. It is current only
	/// while a watcher keeps it so; read it through that watcher rather than directly.
	/// </summary>
	public WorkspaceInventorySnapshot? LastSnapshot => Volatile.Read(ref _lastSnapshot);

	/// <summary>Reloads the whole inventory. A listing failure throws rather than yielding a partial snapshot.</summary>
	public async Task<WorkspaceInventoryChange> RefreshAsync(CancellationToken ct = default) {
		await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
		try {
			return await ReloadAsync(ct).ConfigureAwait(false);
		} finally {
			_refreshGate.Release();
		}
	}

	private async Task<WorkspaceInventoryChange> ReloadAsync(CancellationToken ct) {
		var paths = await _load(ct).ConfigureAwait(false);
		var (files, directories) = Canonicalize(
			[.. paths.Where(path => !path.EndsWith('/'))],
			[.. paths.Where(path => path.EndsWith('/'))]);
		var previous = LastSnapshot ?? Empty;
		return Commit(
			previous,
			[.. files.Where(path => !Spells(previous.Files, path))],
			[.. previous.Files.Where(path => !Spells(files, path))],
			[.. directories.Where(path => !Spells(previous.Directories, path))],
			[.. previous.Directories.Where(path => !Spells(directories, path))]);
	}

	/// <summary>
	/// Re-lists only the entries at or beneath <paramref name="paths"/> (absolute, inside <see cref="Root"/>) and
	/// keeps the rest of the previous snapshot; the result matches a full refresh for those paths, at a cost that
	/// follows the listed paths rather than the workspace. Refreshes fully before a snapshot exists.
	/// Pass <see cref="CoveringScope"/>'s result to list a burst of related paths as one query.
	/// </summary>
	public async Task<WorkspaceInventoryChange> RefreshAsync(IReadOnlyCollection<string> paths, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(paths);
		var scope = paths.Select(path => WorkspacePaths.CanonicalFsPath(PathIdentity.Normalize(path))).ToHashSet(PathIdentity.CanonicalComparer);
		if (LastSnapshot is null || scope.Contains(Root)) {
			return await RefreshAsync(ct).ConfigureAwait(false);
		}

		await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
		try {
			var previous = LastSnapshot!;
			// Every known entry beneath a scope path implies that path is known, so unknown scope paths drop nothing.
			// Known paths are always listed: an ignored directory can still hold tracked files.
			var discovered = scope.Where(path => !previous.Files.Contains(path) && !previous.Directories.Contains(path)).ToList();
			scope.ExceptWith(discovered);
			var listed = await _loadScope(scope, discovered, ct).ConfigureAwait(false);
			if (listed is null) {
				return await ReloadAsync(ct).ConfigureAwait(false);
			}

			var (files, directories) = Canonicalize(
				[.. listed.Where(path => !path.EndsWith('/'))],
				[.. listed.Where(path => path.EndsWith('/'))]);
			return Commit(
				previous,
				[.. files.Where(path => !Spells(previous.Files, path))],
				[.. scope.SelectMany(path => AtOrBeneath(previous.Files, path)).Where(path => !Spells(files, path)).Distinct(PathIdentity.CanonicalComparer)],
				[.. directories.Where(path => !Spells(previous.Directories, path))],
				[.. scope.SelectMany(path => AtOrBeneath(previous.Directories, path)).Where(path => !Spells(directories, path)).Distinct(PathIdentity.CanonicalComparer)]);
		} finally {
			_refreshGate.Release();
		}
	}

	private static readonly WorkspaceInventorySnapshot Empty = new(
		0,
		ImmutableSortedSet.Create<string>(PathIdentity.CanonicalComparer),
		ImmutableSortedSet.Create<string>(PathIdentity.CanonicalComparer));

	private WorkspaceInventoryChange Commit(
		WorkspaceInventorySnapshot previous,
		string[] createdFiles,
		string[] deletedFiles,
		string[] addedDirectories,
		string[] removedDirectories) {
		var change = new WorkspaceInventoryChange(previous, createdFiles, deletedFiles, addedDirectories, removedDirectories);
		if (change.IsEmpty) {
			return change;
		}

		change = change with {
			Snapshot = new WorkspaceInventorySnapshot(
				previous.Revision + 1,
				previous.Files.Except(deletedFiles).Union(createdFiles),
				previous.Directories.Except(removedDirectories).Union(addedDirectories)),
		};
		Volatile.Write(ref _lastSnapshot, change.Snapshot);
		Changed?.Invoke(change);
		return change;
	}

	// Whether `paths` holds `path` in this exact spelling: where paths compare without case, a rename that only
	// changes case is the old spelling deleted and the new one created.
	private static bool Spells(ImmutableSortedSet<string> paths, string path) =>
		paths.TryGetValue(path, out string held) && string.Equals(held, path, StringComparison.Ordinal);

	private static bool Spells(HashSet<string> paths, string path) =>
		paths.TryGetValue(path, out string? held) && string.Equals(held, path, StringComparison.Ordinal);

	private static IEnumerable<string> AtOrBeneath(ImmutableSortedSet<string> paths, string path) =>
		paths.Contains(path) ? Beneath(paths, path).Prepend(path) : Beneath(paths, path);

	// Paths sort ordinally, so everything beneath a directory is one contiguous run after its separator-ended name.
	internal static IEnumerable<string> Beneath(ImmutableSortedSet<string> paths, string directory) {
		string prefix = directory.EndsWith(Path.DirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar;
		int index = paths.IndexOf(prefix);
		for (index = index < 0 ? ~index : index + 1;
			index < paths.Count && paths[index].StartsWith(prefix, PathIdentity.Comparison);
			index++) {
			yield return paths[index];
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
		return paths
			.Select(path => HighestUnknownAncestor(snapshot.Directories, WorkspacePaths.CanonicalFsPath(PathIdentity.Normalize(path))))
			.ToHashSet(PathIdentity.CanonicalComparer);
	}

	// A known directory, the root above all, is already its own covering scope: nothing above the root is listed.
	private static string HighestUnknownAncestor(ImmutableSortedSet<string> directories, string path) {
		if (directories.Contains(path)) {
			return path;
		}

		for (string? parent = Path.GetDirectoryName(path);
			parent is not null && !directories.Contains(parent);
			parent = Path.GetDirectoryName(parent)) {
			path = parent;
		}

		return path;
	}

	// The canonical absolute files, and the directories with every file ancestor and the root added.
	internal (HashSet<string> Files, HashSet<string> Directories) Canonicalize(
		IReadOnlyList<string> relativeFiles,
		IReadOnlyList<string> relativeDirectories) {
		var files = new HashSet<string>(PathIdentity.CanonicalComparer);
		var directories = new HashSet<string>(PathIdentity.CanonicalComparer) { Root };
		foreach (string relative in relativeFiles) {
			string fullPath = WorkspacePaths.CanonicalFsPath(PathIdentity.Normalize(relative, Root));
			if (!fullPath.StartsWith(_rootPrefix, PathIdentity.Comparison)) {
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
			if (!PathIdentity.CanonicalComparer.Equals(fullPath, Root) && !fullPath.StartsWith(_rootPrefix, PathIdentity.Comparison)) {
				throw new GitException($"Workspace inventory returned a path outside the workspace: {relative}");
			}

			directories.Add(fullPath);
		}

		return (files, directories);
	}
}
