using Weavie.Core.FileSystem;
using Weavie.Core.Git;

namespace Weavie.Core.Workspaces;

/// <summary>An authoritative workspace snapshot and the flat directories needed to observe its files.</summary>
/// <param name="IsRepository">Whether Git supplied the snapshot.</param>
/// <param name="Files">Canonical absolute paths for tracked and untracked, non-ignored files.</param>
/// <param name="Directories">Canonical absolute parent directories from each file up to the workspace root.</param>
public sealed record WorkspaceInventorySnapshot(
	bool IsRepository,
	IReadOnlyList<string> Files,
	IReadOnlyList<string> Directories);

internal readonly record struct WorkspaceFileMove(string OldPath, string NewPath);

/// <summary>
/// Loads one workspace's tracked and untracked files from Git and derives their parent directories without
/// walking the workspace. Refreshes are serialized so every consumer sees a complete snapshot.
/// </summary>
public sealed partial class WorkspaceInventory {
	private readonly Func<CancellationToken, Task<IReadOnlyList<string>?>> _load;
	private readonly SemaphoreSlim _refreshGate = new(1, 1);
	private readonly Lock _knownFilesLock = new();
	private readonly HashSet<string> _knownNonRepositoryFiles = new(PathIdentity.Comparer);
	private readonly HashSet<string> _knownNonRepositoryDirectories = new(PathIdentity.Comparer);
	private bool? _isRepository;

	/// <summary>Creates a Git-backed inventory rooted at <paramref name="root"/>.</summary>
	public WorkspaceInventory(string root) : this(root, ct => new GitService().ListWorkspaceFilesAsync(root, ct)) { }

	internal WorkspaceInventory(
		string root,
		Func<CancellationToken, Task<IReadOnlyList<string>?>> load) {
		ArgumentException.ThrowIfNullOrEmpty(root);
		ArgumentNullException.ThrowIfNull(load);
		Root = WorkspacePaths.CanonicalFsPath(PathIdentity.Normalize(root));
		_load = load;
	}

	/// <summary>The canonical absolute workspace root.</summary>
	public string Root { get; }

	/// <summary>Raised when a non-Git consumer supplies a newer already-known file set.</summary>
	public event Action? Changed;

	/// <summary>
	/// Reloads the authoritative Git inventory. A non-repository snapshot contains paths already supplied by
	/// navigation; Git execution failures throw rather than silently switching to a filesystem walk.
	/// </summary>
	public async Task<WorkspaceInventorySnapshot> RefreshAsync(CancellationToken ct = default) {
		await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
		try {
			var relativeFiles = _isRepository is false
				? null
				: await _load(ct).ConfigureAwait(false);
			if (relativeFiles is not null) {
				_isRepository = true;
				return BuildSnapshot(isRepository: true, relativeFiles, []);
			}

			_isRepository = false;
			lock (_knownFilesLock) {
				return BuildSnapshot(
					isRepository: false,
					[.. _knownNonRepositoryFiles],
					[.. _knownNonRepositoryDirectories]);
			}
		} finally {
			_refreshGate.Release();
		}
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
