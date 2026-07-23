using System.Security;
using System.Text.Json;

namespace Weavie.Core.Changes;

public sealed partial class SessionChangeTracker {
	private const int CheckpointVersion = 1;
	private static readonly JsonSerializerOptions CheckpointJson = new() {
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

	private readonly IReviewCheckpointStore _reviewStore;
	private string? _lastCheckpointDocument;
	private bool _checkpointPending;
	private readonly HashSet<string> _pendingCheckpointPaths = new(StringComparer.Ordinal);
	private ReviewIdentity? _reviewIdentity;
	private long _armToken;
	private long _activeReviewToken;
	private readonly List<ReviewProblem> _reviewProblems = [];

	/// <summary>Raised after the durable review problem set changes.</summary>
	public event Action? ReviewProblemsChanged;

	/// <summary>The PR or ref review currently armed over this tracker.</summary>
	public ReviewIdentity? ActiveReviewIdentity {
		get { lock (_gate) { return _reviewIdentity; } }
	}

	/// <summary>The monotonic token fencing asynchronous arm and comment operations.</summary>
	public long ArmToken {
		get { lock (_gate) { return _armToken; } }
	}

	/// <summary>The token belonging to <see cref="ActiveReviewIdentity"/>, or zero when no review is armed.</summary>
	public long ActiveReviewToken {
		get { lock (_gate) { return _activeReviewToken; } }
	}

	/// <summary>Problems encountered while restoring or checkpointing this review.</summary>
	public IReadOnlyList<ReviewProblem> ReviewProblems {
		get { lock (_gate) { return [.. _reviewProblems]; } }
	}

	/// <summary>Reserves a token for an asynchronous review-arm operation.</summary>
	public long BeginReviewArm() {
		lock (_gate) {
			return ++_armToken;
		}
	}

	/// <summary>
	/// Atomically replaces the current board with <paramref name="seeds"/> and arms
	/// <paramref name="identity"/> when <paramref name="token"/> is still current.
	/// </summary>
	public bool ArmReview(long token, ReviewIdentity identity, IReadOnlyList<ReviewSeed> seeds) {
		ArgumentNullException.ThrowIfNull(identity);
		ArgumentNullException.ThrowIfNull(seeds);
		if (!PathsEqual(identity.Worktree, _workspaceRoot)) {
			throw new ArgumentException("Review identity belongs to a different worktree.", nameof(identity));
		}
		var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
		var paths = new HashSet<string>(pathComparer);
		var normalizedSeeds = new List<ReviewSeed>(seeds.Count);
		foreach (var seed in seeds) {
			ArgumentException.ThrowIfNullOrEmpty(seed.Path);
			ArgumentNullException.ThrowIfNull(seed.RefContent);
			ArgumentNullException.ThrowIfNull(seed.DiskContent);
			string path = Path.GetFullPath(seed.Path);
			if (!_isInScope(path)) {
				throw new ArgumentException($"Review seed '{seed.Path}' is outside the session scope.", nameof(seeds));
			}
			if (!paths.Add(path)) {
				throw new ArgumentException($"Review seed '{seed.Path}' is duplicated.", nameof(seeds));
			}
			normalizedSeeds.Add(seed with { Path = path });
		}

		return MutateReview(rollbackOnFailure: true, () => {
			if (token != _armToken || normalizedSeeds.Any(seed => !DiskMatchesSeedLocked(seed))) {
				return ReviewUnchanged(false);
			}

			var dirtyPaths = ReviewStatePathsLocked();
			AcceptTurnLocked(clearLocalReview: false);
			foreach (var seed in normalizedSeeds) {
				SeedRefBaselineLocked(seed.Path, seed.RefContent, seed.DiskContent, seed.ExistedAtRef);
				dirtyPaths.Add(seed.Path);
			}

			_reviewIdentity = identity;
			_activeReviewToken = token;
			return ReviewChanged(true, dirtyPaths);
		});
	}

	/// <summary>
	/// Atomically commits and retracts the armed review when <paramref name="token"/> is still current.
	/// </summary>
	public bool RetractReview(long token) {
		lock (_gate) {
			if (token != _armToken || _reviewIdentity is null) {
				return false;
			}
		}

		return MutateReview(rollbackOnFailure: true, () => {
			if (token != _armToken || _reviewIdentity is null) {
				return ReviewUnchanged(false);
			}

			var dirtyPaths = ReviewStatePathsLocked();
			AcceptTurnLocked(clearLocalReview: false);
			_reviewIdentity = null;
			_activeReviewToken = 0;
			return ReviewChanged(true, dirtyPaths);
		});
	}

	private bool DiskMatchesSeedLocked(ReviewSeed seed) {
		bool exists = _fileSystem.FileExists(seed.Path);
		if (exists != seed.ExistsOnDisk) {
			return false;
		}

		return !exists || (_fileSystem.TryReadAllText(seed.Path, out string content)
			&& string.Equals(content, seed.DiskContent, StringComparison.Ordinal));
	}

	private void InitializePersistence() {
		lock (_gate) {
			try {
				string? document = _reviewStore.Load();
				if (document is null) {
					return;
				}

				var checkpoint = JsonSerializer.Deserialize<ReviewCheckpoint>(document, CheckpointJson)
					?? throw new InvalidDataException("Review checkpoint is empty.");
				if (checkpoint.Version != CheckpointVersion) {
					throw new InvalidDataException($"Review checkpoint version {checkpoint.Version} is not supported.");
				}

				ReviewCheckpointValidator.Validate(checkpoint);
				RestoreCheckpointLocked(checkpoint);
				_lastCheckpointDocument = document;
				_checkpointPending = true;
				_pendingCheckpointPaths.UnionWith(_current.Keys);
				try {
					CommitReviewLocked();
				} catch (Exception ex) when (IsPersistenceFailure(ex)) {
					SetProblemLocked(string.Empty, $"Review state could not be saved: {ex.Message}");
				}
			} catch (Exception ex) when (IsPersistenceFailure(ex)) {
				SetProblemLocked(string.Empty, $"Review state could not be restored: {ex.Message}");
			}
		}
	}

	private bool CommitReviewLocked() {
		bool problemsChanged = false;
		CheckpointProjection projection;
		while (true) {
			projection = ProjectCheckpointLocked(out var unreadableGuardPaths);
			if (unreadableGuardPaths.Count == 0) {
				break;
			}

			foreach (string path in unreadableGuardPaths) {
				InvalidateHistoryForPathLocked(path);
				string relative = Relativize(path);
				problemsChanged |= SetProblemLocked(
					relative,
					$"Review history for {relative} was invalidated because the file is no longer readable as text.");
			}
		}
		string? document = projection.Document;
		if (string.Equals(document, _lastCheckpointDocument, StringComparison.Ordinal)) {
			PromoteCheckpointProjectionLocked(projection);
			return problemsChanged | ClearPersistenceProblemLocked();
		}

		if (document is null) {
			_reviewStore.Clear();
		} else {
			_reviewStore.Save(document);
		}
		_lastCheckpointDocument = document;
		PromoteCheckpointProjectionLocked(projection);
		return problemsChanged | ClearPersistenceProblemLocked();
	}

	private void PromoteCheckpointProjectionLocked(CheckpointProjection projection) {
		_checkpointFiles = projection.Files;
		_checkpointGuards = projection.Guards;
		_checkpointGuardObservations = projection.GuardObservations;
		_checkpointPending = false;
		_pendingCheckpointPaths.Clear();
	}

	private void AddInvalidatedLocked(string path, string reason) =>
		SetProblemLocked(path, $"Saved review state for {path} was invalidated because {reason}.");

	private bool SetProblemLocked(string path, string message) {
		var problem = new ReviewProblem(path, message);
		if (_reviewProblems.Contains(problem)) {
			return false;
		}
		_reviewProblems.Add(problem);
		return true;
	}

	private bool ClearPersistenceProblemLocked() =>
		_reviewProblems.RemoveAll(problem => problem.Path.Length == 0
			&& problem.Message.StartsWith("Review state could not be saved", StringComparison.Ordinal)) > 0;

	private static bool IsPersistenceFailure(Exception ex) =>
		ex is IOException
			or UnauthorizedAccessException
			or JsonException
			or InvalidDataException
			or NotSupportedException
			or SecurityException;

}
