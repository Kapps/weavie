using System.Text.Json;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Changes;

public sealed partial class SessionChangeTracker {
	private Dictionary<string, ReviewFileCheckpoint> _checkpointFiles = new(StringComparer.Ordinal);
	private Dictionary<string, ReviewDiskGuardCheckpoint> _checkpointGuards = new(StringComparer.Ordinal);
	private Dictionary<string, ReviewGuardObservation> _checkpointGuardObservations = new(StringComparer.Ordinal);

	private CheckpointProjection ProjectCheckpointLocked(out List<string> unreadableGuardPaths) {
		unreadableGuardPaths = [];
		var paths = _acceptedAnchor
			.Where(pair => _current.TryGetValue(pair.Key, out string? current)
				&& !string.Equals(pair.Value, current, StringComparison.Ordinal))
			.Select(pair => pair.Key)
			.Order(StringComparer.Ordinal)
			.ToList();
		var visiblePaths = paths.ToHashSet(StringComparer.Ordinal);
		var guardPaths = _undoStack
			.Concat(_redoStack)
			.SelectMany(action => action.Before.Concat(action.After))
			.Select(state => state.Path)
			.Where(path => !visiblePaths.Contains(path))
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal)
			.ToList();

		var files = new Dictionary<string, ReviewFileCheckpoint>(_checkpointFiles, StringComparer.Ordinal);
		foreach (string stale in files.Keys.Where(path => !visiblePaths.Contains(path)).ToList()) {
			files.Remove(stale);
		}
		foreach (string path in paths) {
			if (_pendingCheckpointPaths.Contains(path) || !files.ContainsKey(path)) {
				files[path] = CreateFileCheckpointLocked(path);
			}
		}

		var guardPathSet = guardPaths.ToHashSet(StringComparer.Ordinal);
		var guards = new Dictionary<string, ReviewDiskGuardCheckpoint>(_checkpointGuards, StringComparer.Ordinal);
		var guardObservations = new Dictionary<string, ReviewGuardObservation>(
			_checkpointGuardObservations,
			StringComparer.Ordinal);
		foreach (string stale in guards.Keys.Where(path => !guardPathSet.Contains(path)).ToList()) {
			guards.Remove(stale);
			guardObservations.Remove(stale);
		}
		foreach (string path in guardPaths) {
			var observation = ObserveGuardLocked(path);
			if (guards.ContainsKey(path)
				&& guardObservations.TryGetValue(path, out var prior)
				&& prior == observation) {
				continue;
			}
			string disk = string.Empty;
			try {
				if (observation.Exists && !_fileSystem.TryReadAllText(path, out disk)) {
					unreadableGuardPaths.Add(path);
					continue;
				}
			} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) {
				unreadableGuardPaths.Add(path);
				continue;
			}
			if (!guards.ContainsKey(path)) {
				guards[path] = new ReviewDiskGuardCheckpoint {
					Path = Relativize(path),
					DiskExists = observation.Exists,
					DiskHash = ReviewTextCodec.Hash(disk),
				};
			}
			guardObservations[path] = observation;
		}
		if (unreadableGuardPaths.Count > 0) {
			return new CheckpointProjection(files, guards, guardObservations, null);
		}

		if (paths.Count == 0 && guardPaths.Count == 0 && _reviewIdentity is null) {
			return new CheckpointProjection(files, guards, guardObservations, null);
		}

		string document = JsonSerializer.Serialize(new ReviewCheckpoint {
			Version = CheckpointVersion,
			Review = _reviewIdentity,
			ArmToken = _armToken,
			ActiveReviewToken = _activeReviewToken,
			NextOriginId = _nextOriginId,
			Files = [.. paths.Select(path => files[path])],
			Guards = [.. guardPaths.Select(path => guards[path])],
		}, CheckpointJson);
		return new CheckpointProjection(files, guards, guardObservations, document);
	}

	private ReviewGuardObservation ObserveGuardLocked(string path) {
		bool exists = _fileSystem.FileExists(path);
		return new ReviewGuardObservation(
			exists,
			exists && _fileSystem.TryGetStat(path, out var stat) ? stat : default);
	}

	private ReviewFileCheckpoint CreateFileCheckpointLocked(string path) {
		string current = _current[path];
		string disk = _provenance.TryGetValue(path, out var provenance) ? provenance.Text : current;
		return new ReviewFileCheckpoint {
			Path = Relativize(path),
			DiskExists = _fileSystem.FileExists(path),
			DiskHash = ReviewTextCodec.Hash(disk),
			CreatedSinceBaseline = _createdSinceBaseline.Contains(path),
			Current = ReviewTextCodec.Encode(disk, current),
			ReviewBaseline = ReviewTextCodec.Encode(disk, _reviewBaseline.GetValueOrDefault(path, string.Empty)),
			AcceptedAnchor = ReviewTextCodec.Encode(disk, _acceptedAnchor.GetValueOrDefault(path, string.Empty)),
			SessionBaseline = ReviewTextCodec.Encode(disk, _baseline.GetValueOrDefault(path, string.Empty)),
			Provenance = EncodeProvenance(provenance),
		};
	}

	private void RestoreCheckpointLocked(ReviewCheckpoint checkpoint) {
		var staged = new List<RestoredReviewFile>();
		var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
		long greatestOrigin = 0;
		bool guardInvalidated = false;
		foreach (var file in checkpoint.Files) {
			string path = ResolveCheckpointPath(file.Path);
			if (!seen.Add(path)) {
				throw new InvalidDataException($"Review checkpoint contains duplicate path '{file.Path}'.");
			}

			bool exists = _fileSystem.FileExists(path);
			string disk;
			if (exists) {
				if (!_fileSystem.TryReadAllText(path, out disk)) {
					AddInvalidatedLocked(file.Path, "the file is no longer readable as text");
					continue;
				}
			} else {
				disk = string.Empty;
			}

			if (exists != file.DiskExists || !string.Equals(ReviewTextCodec.Hash(disk), file.DiskHash, StringComparison.Ordinal)) {
				AddInvalidatedLocked(file.Path, "the file changed after the review checkpoint");
				continue;
			}

			try {
				string current = ReviewTextCodec.Decode(disk, file.Current);
				string reviewBaseline = ReviewTextCodec.Decode(disk, file.ReviewBaseline);
				string acceptedAnchor = ReviewTextCodec.Decode(disk, file.AcceptedAnchor);
				string baseline = ReviewTextCodec.Decode(disk, file.SessionBaseline);
				var provenance = DecodeProvenance(disk, file.Provenance, ref greatestOrigin);
				staged.Add(new RestoredReviewFile(
					path, baseline, current, reviewBaseline, acceptedAnchor, disk, provenance, file.CreatedSinceBaseline));
			} catch (InvalidDataException ex) {
				AddInvalidatedLocked(file.Path, ex.Message);
			}
		}
		foreach (var guard in checkpoint.Guards) {
			string path = ResolveCheckpointPath(guard.Path);
			if (!seen.Add(path)) {
				throw new InvalidDataException($"Review checkpoint contains duplicate path '{guard.Path}'.");
			}

			bool exists = _fileSystem.FileExists(path);
			string disk;
			if (exists) {
				if (!_fileSystem.TryReadAllText(path, out disk)) {
					AddInvalidatedLocked(guard.Path, "the file is no longer readable as text");
					guardInvalidated = true;
					continue;
				}
			} else {
				disk = string.Empty;
			}

			if (exists != guard.DiskExists
				|| !string.Equals(ReviewTextCodec.Hash(disk), guard.DiskHash, StringComparison.Ordinal)) {
				AddInvalidatedLocked(guard.Path, "the file changed after the review checkpoint");
				guardInvalidated = true;
			}
		}

		try {
			if (checkpoint.Review is { } identity && !PathsEqual(identity.Worktree, _workspaceRoot)) {
				throw new InvalidDataException("Review checkpoint belongs to a different worktree.");
			}
		} catch (Exception ex) when (ex is ArgumentException or NotSupportedException) {
			throw new InvalidDataException("Review checkpoint contains an invalid worktree path.", ex);
		}
		if ((checkpoint.Review is null) != (checkpoint.ActiveReviewToken == 0)
			|| checkpoint.ArmToken < checkpoint.ActiveReviewToken) {
			throw new InvalidDataException("Review checkpoint contains an invalid arm token.");
		}
		if (checkpoint.NextOriginId < greatestOrigin) {
			throw new InvalidDataException("Review checkpoint contains an invalid provenance counter.");
		}

		foreach (var file in staged) {
			_baseline[file.Path] = file.Baseline;
			_current[file.Path] = file.Current;
			_reviewBaseline[file.Path] = file.ReviewBaseline;
			_acceptedAnchor[file.Path] = file.AcceptedAnchor;
			_preEdit[file.Path] = file.Disk;
			_provenance[file.Path] = file.Provenance;
			if (file.CreatedSinceBaseline) {
				_createdSinceBaseline.Add(file.Path);
			}
		}

		bool reviewRestored = checkpoint.Files.Count > 0 ? staged.Count > 0 : !guardInvalidated;
		_reviewIdentity = reviewRestored ? checkpoint.Review : null;
		_armToken = checkpoint.ArmToken;
		_activeReviewToken = reviewRestored ? checkpoint.ActiveReviewToken : 0;
		_nextOriginId = checkpoint.NextOriginId;
	}

	private static bool PathsEqual(string left, string right) =>
		string.Equals(
			Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
			Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
			OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

	private string ResolveCheckpointPath(string storedPath) {
		string path;
		try {
			path = Path.GetFullPath(Path.IsPathRooted(storedPath)
				? storedPath
				: Path.Combine(_workspaceRoot, storedPath.Replace('/', Path.DirectorySeparatorChar)));
		} catch (Exception ex) when (ex is ArgumentException or NotSupportedException) {
			throw new InvalidDataException($"Review checkpoint path '{storedPath}' is invalid.", ex);
		}
		if (!_isInScope(path)) {
			throw new InvalidDataException($"Review checkpoint path '{storedPath}' is outside the session scope.");
		}
		return path;
	}

	private sealed record CheckpointProjection(
		Dictionary<string, ReviewFileCheckpoint> Files,
		Dictionary<string, ReviewDiskGuardCheckpoint> Guards,
		Dictionary<string, ReviewGuardObservation> GuardObservations,
		string? Document);

	private readonly record struct ReviewGuardObservation(bool Exists, FileStat Stat);
}
