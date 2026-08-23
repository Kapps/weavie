namespace Weavie.Core.Workspaces;

/// <summary>Identifies one serialized non-repository navigation snapshot while watcher events continue.</summary>
/// <param name="Id">The inventory-owned snapshot identifier.</param>
public readonly record struct NonRepositoryInventorySeed(long Id);

public sealed partial class WorkspaceInventory {
	private readonly SemaphoreSlim _nonRepositorySeedGate = new(1, 1);
	private readonly List<NonRepositoryMutation> _nonRepositoryMutations = [];
	private long _nextSeedId;
	private long _activeSeedId;

	/// <summary>Begins a serialized non-Git navigation snapshot without blocking watcher event capture.</summary>
	public async Task<NonRepositoryInventorySeed> BeginNonRepositorySeedAsync(CancellationToken ct = default) {
		await _nonRepositorySeedGate.WaitAsync(ct).ConfigureAwait(false);
		lock (_knownFilesLock) {
			_activeSeedId = ++_nextSeedId;
			_nonRepositoryMutations.Clear();
			return new NonRepositoryInventorySeed(_activeSeedId);
		}
	}

	/// <summary>
	/// Replaces the non-Git navigation snapshot and replays every watcher mutation observed since it began.
	/// </summary>
	public WorkspaceInventorySnapshot CompleteNonRepositorySeed(
		NonRepositoryInventorySeed seed,
		IReadOnlyList<string> files,
		IReadOnlyList<string> directories) {
		ArgumentNullException.ThrowIfNull(files);
		ArgumentNullException.ThrowIfNull(directories);
		string[] relativeFiles = [.. files.Select(file => Path.GetRelativePath(Root, file))];
		string[] relativeDirectories = [.. directories.Select(directory => Path.GetRelativePath(Root, directory))];

		WorkspaceInventorySnapshot snapshot;
		lock (_knownFilesLock) {
			ValidateActiveSeed(seed);
			_knownNonRepositoryFiles.Clear();
			_knownNonRepositoryDirectories.Clear();
			_knownNonRepositoryDirectories.UnionWith(relativeDirectories);
			foreach (string file in relativeFiles) {
				ApplyTrackFile(file);
			}

			foreach (var mutation in _nonRepositoryMutations) {
				ApplyMutation(mutation);
			}
			snapshot = BuildSnapshot(
				isRepository: false,
				[.. _knownNonRepositoryFiles],
				[.. _knownNonRepositoryDirectories]);

			_activeSeedId = 0;
			_nonRepositoryMutations.Clear();
		}

		_nonRepositorySeedGate.Release();
		Changed?.Invoke();
		return snapshot;
	}

	/// <summary>Cancels a failed non-Git navigation snapshot while preserving watcher-owned state.</summary>
	public void CancelNonRepositorySeed(NonRepositoryInventorySeed seed) {
		lock (_knownFilesLock) {
			ValidateActiveSeed(seed);
			_activeSeedId = 0;
			_nonRepositoryMutations.Clear();
		}

		_nonRepositorySeedGate.Release();
	}

	internal void TrackNonRepositoryFile(string path) {
		string relative = Path.GetRelativePath(Root, path);
		bool changed;
		lock (_knownFilesLock) {
			RecordMutation(NonRepositoryMutationKind.TrackFile, relative, string.Empty);
			changed = ApplyTrackFile(relative);
		}

		if (changed) {
			Changed?.Invoke();
		}
	}

	internal void TrackNonRepositoryDirectory(string path) {
		string relative = Path.GetRelativePath(Root, path);
		bool changed;
		lock (_knownFilesLock) {
			RecordMutation(NonRepositoryMutationKind.TrackDirectory, relative, string.Empty);
			changed = _knownNonRepositoryDirectories.Add(relative);
		}

		if (changed) {
			Changed?.Invoke();
		}
	}

	internal bool ForgetNonRepositoryFile(string path) {
		string relative = Path.GetRelativePath(Root, path);
		bool removed;
		lock (_knownFilesLock) {
			RecordMutation(NonRepositoryMutationKind.ForgetFile, relative, string.Empty);
			removed = _knownNonRepositoryFiles.Remove(relative);
		}

		if (removed) {
			Changed?.Invoke();
		}

		return removed;
	}

	internal bool IsKnownNonRepositoryDirectory(string path) {
		string relative = Path.GetRelativePath(Root, path);
		lock (_knownFilesLock) {
			return _knownNonRepositoryDirectories.Contains(relative);
		}
	}

	internal IReadOnlyList<string> ForgetNonRepositoryTree(string path) {
		string relative = Path.GetRelativePath(Root, path);
		var removed = new List<string>();
		lock (_knownFilesLock) {
			RecordMutation(NonRepositoryMutationKind.ForgetTree, relative, string.Empty);
			ApplyForgetTree(relative, removed);
		}

		Changed?.Invoke();
		return removed;
	}

	internal IReadOnlyList<WorkspaceFileMove> MoveNonRepositoryTree(string oldPath, string newPath) {
		string oldRelative = Path.GetRelativePath(Root, oldPath);
		string newRelative = Path.GetRelativePath(Root, newPath);
		var moved = new List<WorkspaceFileMove>();
		lock (_knownFilesLock) {
			RecordMutation(NonRepositoryMutationKind.MoveTree, oldRelative, newRelative);
			ApplyMoveTree(oldRelative, newRelative, moved);
		}

		Changed?.Invoke();
		return moved;
	}

	private static bool IsWithin(string path, string root) =>
		string.Equals(path, root, PathComparison)
		|| path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison)
		|| path.StartsWith(root + Path.AltDirectorySeparatorChar, PathComparison);

	private static string Rebase(string path, string oldRoot, string newRoot) =>
		newRoot + path[oldRoot.Length..];

	private void ValidateActiveSeed(NonRepositoryInventorySeed seed) {
		if (seed.Id == 0 || seed.Id != _activeSeedId) {
			throw new InvalidOperationException("The non-repository inventory seed is not active.");
		}
	}

	private void RecordMutation(NonRepositoryMutationKind kind, string path, string destination) {
		if (_activeSeedId != 0) {
			_nonRepositoryMutations.Add(new NonRepositoryMutation(kind, path, destination));
		}
	}

	private void ApplyMutation(NonRepositoryMutation mutation) {
		switch (mutation.Kind) {
			case NonRepositoryMutationKind.TrackFile:
				ApplyTrackFile(mutation.Path);
				break;
			case NonRepositoryMutationKind.ForgetFile:
				_knownNonRepositoryFiles.Remove(mutation.Path);
				break;
			case NonRepositoryMutationKind.TrackDirectory:
				_knownNonRepositoryDirectories.Add(mutation.Path);
				break;
			case NonRepositoryMutationKind.ForgetTree:
				ApplyForgetTree(mutation.Path, null);
				break;
			case NonRepositoryMutationKind.MoveTree:
				ApplyMoveTree(mutation.Path, mutation.Destination, null);
				break;
			default:
				throw new InvalidOperationException($"Unknown non-repository mutation: {mutation.Kind}");
		}
	}

	/// <summary>Tracks <paramref name="file"/>, reporting whether the inventory learned a path it lacked.</summary>
	private bool ApplyTrackFile(string file) {
		bool added = _knownNonRepositoryFiles.Add(file);
		AddParentDirectories(file);
		return added;
	}

	private void ApplyForgetTree(string root, List<string>? removed) {
		foreach (string file in _knownNonRepositoryFiles.Where(file => IsWithin(file, root)).ToArray()) {
			_knownNonRepositoryFiles.Remove(file);
			removed?.Add(Path.GetFullPath(Path.Combine(Root, file)));
		}

		_knownNonRepositoryDirectories.RemoveWhere(directory => IsWithin(directory, root));
	}

	private void ApplyMoveTree(string oldRoot, string newRoot, List<WorkspaceFileMove>? moved) {
		foreach (string file in _knownNonRepositoryFiles.Where(file => IsWithin(file, oldRoot)).ToArray()) {
			string destination = Rebase(file, oldRoot, newRoot);
			_knownNonRepositoryFiles.Remove(file);
			_knownNonRepositoryFiles.Add(destination);
			moved?.Add(new WorkspaceFileMove(
				Path.GetFullPath(Path.Combine(Root, file)),
				Path.GetFullPath(Path.Combine(Root, destination))));
		}

		foreach (string directory in _knownNonRepositoryDirectories.Where(directory => IsWithin(directory, oldRoot)).ToArray()) {
			_knownNonRepositoryDirectories.Remove(directory);
			_knownNonRepositoryDirectories.Add(Rebase(directory, oldRoot, newRoot));
		}
	}

	private void AddParentDirectories(string file) {
		for (string? directory = Path.GetDirectoryName(file);
			!string.IsNullOrEmpty(directory) && directory != ".";
			directory = Path.GetDirectoryName(directory)) {
			_knownNonRepositoryDirectories.Add(directory);
		}
	}

	private readonly record struct NonRepositoryMutation(
		NonRepositoryMutationKind Kind,
		string Path,
		string Destination);

	private enum NonRepositoryMutationKind {
		TrackFile,
		ForgetFile,
		TrackDirectory,
		ForgetTree,
		MoveTree,
	}
}
