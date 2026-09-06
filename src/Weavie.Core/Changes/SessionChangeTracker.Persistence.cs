using System.Text.Json;
using Weavie.Core.FileSystem;
using Weavie.Core.Review;

namespace Weavie.Core.Changes;

public sealed partial class SessionChangeTracker {
	private readonly IReviewPersistence _persistence;
	private ReviewContext? _review;
	private bool _restoring;

	/// <summary>The persisted PR/ref identity, independent of the loaded session incarnation.</summary>
	public ReviewContext? Review { get { lock (_gate) return _review; } }

	private void Checkpoint() {
		if (_restoring) return;
		SynchronizeHistory(external: true, except: null);
		_persistence.Save(JsonSerializer.Serialize(new ReviewSnapshot(
			_workspaceRoot, [.. _baseline.Keys.Select(path => Capture(path, withDisk: false))],
			_undoStack, _redoStack, _review, _nextOriginId, _nextActionId, _currentPrompt)));
	}

	private void RestoreCheckpoint() {
		ArgumentNullException.ThrowIfNull(_persistence);
		if (_persistence.Read() is not { } document) return;
		try {
			var saved = JsonSerializer.Deserialize<ReviewSnapshot>(document)
				?? throw new JsonException("The review document is empty.");
			if (!PathIdentity.Equals(saved.Root, _workspaceRoot) || saved.Files is null || saved.Undo is null || saved.Redo is null
				|| saved.Review is { } review && !PathIdentity.Equals(review.Worktree, _workspaceRoot))
				throw new JsonException("The review does not belong to this worktree.");
			foreach (var file in saved.Files) {
				if (file is null || !file.Tracked || file.Path is null || !Path.IsPathFullyQualified(file.Path) || !_isInScope(file.Path)
					|| file.Baseline is null || file.Current is null || file.ReviewBaseline is null || file.AcceptedAnchor is null
					|| file.PreEdit is null || file.Disk is null)
					throw new JsonException("The review contains an out-of-scope file.");
				if (file.Provenance is { } provenance && (provenance.Text is null || provenance.Lines is null
					|| provenance.Lines.Count != LineDiff.SplitLines(provenance.Text).Length
					|| !ValidGaps(provenance.DeletedAtGap, provenance.Lines.Count)))
					throw new JsonException("The review contains invalid file provenance.");
			}
			var paths = saved.Files.Select(file => file.Path).ToHashSet(PathIdentity.Comparer);
			foreach (var action in saved.Undo.Concat(saved.Redo)) {
				if (action is null || !Enum.IsDefined(action.Kind) || action.Patches is null || action.Patches.Count == 0
					|| action.Patches.Any(patch => !ValidPatch(patch, paths)))
					throw new JsonException("The review contains an invalid decision.");
			}
			var patches = saved.Undo.Concat(saved.Redo).SelectMany(action => action.Patches).ToArray();
			if (patches.Select(patch => patch.Id).Distinct().Count() != patches.Length
				|| patches.Any(patch => patch.Id > saved.NextActionId))
				throw new JsonException("The review contains invalid decision identities.");
			_restoring = true;
			foreach (var file in saved.Files) {
				RestoreState(file);
				_historyHeads[file.Path] = file;
			}
			_undoStack.AddRange(saved.Undo);
			_redoStack.AddRange(saved.Redo);
			_review = saved.Review;
			_nextOriginId = saved.NextOriginId;
			_nextActionId = saved.NextActionId;
			_currentPrompt = saved.Prompt;
			ReconcileReviewDisk();
		} catch (Exception error) when (error is JsonException or ArgumentException) {
			throw new IOException("The saved review could not be restored; its document was left untouched.", error);
		} finally { _restoring = false; }
	}

	// Restore and history actions inspect only paths already owned by this review; never rewrite disk on load.
	private void ReconcileReviewDisk() {
		foreach (string path in _baseline.Keys.ToArray()) {
			bool exists = _fileSystem.FileExists(path);
			if (!TryReadOrEmpty(path, out string actual)) {
				SuspendReview(path);
				if (_fileSystem.TryGetStat(path, out var stat)) _nonText[path] = stat;
				continue;
			}
			if (!exists) {
				_current[path] = string.Empty;
				_missingCurrent.Add(path);
				if (_provenance.TryGetValue(path, out var provenance)) RebaseProvenance(provenance, string.Empty, []);
			} else if (_missingCurrent.Remove(path)) {
				_current[path] = actual;
				_provenance[path] = ProvenanceFile.Empty(actual);
			} else if (!_provenance.TryGetValue(path, out var provenance) || provenance.Text != actual) {
				if (_review is not null) {
					_current[path] = actual;
					CaptureProvenanceBaseline(path, actual);
				} else CaptureHandEdit(path, actual);
			}
		}
		SynchronizeHistory(external: true, except: null);
	}

	private sealed record ReviewSnapshot(string Root, PathState[] Files, List<ReviewAction> Undo,
		List<ReviewAction> Redo, ReviewContext? Review, long NextOriginId, long NextActionId, string? Prompt);
}
