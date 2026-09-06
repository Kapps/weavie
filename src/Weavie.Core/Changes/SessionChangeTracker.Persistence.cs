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

	private readonly HashSet<string> _dirtyFiles = new(PathIdentity.Comparer);
	private readonly HashSet<string> _historyDirty = new(PathIdentity.Comparer);
	private readonly HashSet<string> _dirtyPrompts = new(StringComparer.Ordinal);
	private string? _savedMetadata;

	private void FileChanged(string path) { _dirtyFiles.Add(path); _historyDirty.Add(path); }
	private static string FileKey(string path) => "file:" + (OperatingSystem.IsWindows()
		? PathIdentity.Normalize(path).ToUpperInvariant() : PathIdentity.Normalize(path));

	private void Checkpoint() {
		if (_restoring) return;
		SynchronizeHistory(external: true, except: null);
		var changes = new Dictionary<string, string?>(StringComparer.Ordinal);
		foreach (string path in _dirtyFiles)
			changes[FileKey(path)] = _baseline.ContainsKey(path) ? JsonSerializer.Serialize(Capture(path, withDisk: false)) : null;
		foreach (var (id, entry) in _dirtyHistory)
			changes["history:" + id] = entry is null ? null : JsonSerializer.Serialize(new StoredHistory(id, entry.Undo, entry.Action.Id, entry.Action.Kind, entry.Action.TouchesDisk, entry.Action.Line));
		foreach (var (key, patches) in _dirtyHistoryPatches)
			changes[key] = patches is null ? null : JsonSerializer.Serialize(patches);
		foreach (string key in _dirtyPrompts)
			changes["prompt:" + key] = _conversationPrompts.TryGetValue(key, out string? prompt) ? JsonSerializer.Serialize(prompt) : null;
		string metadata = JsonSerializer.Serialize(new ReviewMetadata(_workspaceRoot, _review, _nextOriginId, _nextActionId, _nextHistoryId));
		if (metadata != _savedMetadata) changes["metadata"] = metadata;
		if (changes.Count == 0) return;
		_persistence.Save(changes);
		_savedMetadata = metadata;
		_dirtyFiles.Clear();
		_dirtyHistory.Clear();
		_dirtyHistoryPatches.Clear();
		_dirtyPrompts.Clear();
	}

	private void RestoreCheckpoint() {
		ArgumentNullException.ThrowIfNull(_persistence);
		var records = _persistence.Read();
		if (records.Count == 0) return;
		try {
			var metadata = Deserialize<ReviewMetadata>(records["metadata"]);
			var files = new List<PathState>();
			var history = new List<StoredHistory>();
			var patchGroups = new List<StoredPatches>();
			var prompts = new Dictionary<string, string?>(StringComparer.Ordinal);
			foreach (var (key, value) in records) {
				if (key == "metadata") continue;
				if (key.StartsWith("file:", StringComparison.Ordinal)) {
					var file = Deserialize<PathState>(value);
					if (file.Path is null || key != FileKey(file.Path)) throw new JsonException("Invalid review file identity.");
					files.Add(file);
				} else if (key.StartsWith("history:", StringComparison.Ordinal)) {
					var entry = Deserialize<StoredHistory>(value);
					if (entry.Id <= 0 || entry.Id > metadata.NextHistoryId || key != "history:" + entry.Id)
						throw new JsonException("Invalid history entry identity.");
					history.Add(entry);
				} else if (key.StartsWith("history-patches:", StringComparison.Ordinal)) {
					var group = Deserialize<StoredPatches>(value);
					if (group.Path is null || key != PatchesKey(group.HistoryId, group.Path) || group.Patches is null
						|| group.Patches.Count == 0 || group.Patches.Any(patch => patch is null || !PathIdentity.Equals(patch.Path, group.Path)))
						throw new JsonException("Invalid history patch group.");
					patchGroups.Add(group);
				} else if (key.StartsWith("prompt:", StringComparison.Ordinal)) prompts.Add(key[7..], JsonSerializer.Deserialize<string?>(value));
				else throw new JsonException("Unknown review record.");
			}
			var groupsByEntry = patchGroups.ToLookup(group => group.HistoryId);
			if (patchGroups.Any(group => !history.Any(entry => entry.Id == group.HistoryId))) throw new JsonException("Orphan history patches.");
			var actions = history.ToDictionary(entry => entry.Id, entry => new ReviewAction(entry.ActionId, entry.Kind,
				entry.TouchesDisk, entry.Line, [.. groupsByEntry[entry.Id].SelectMany(group => group.Patches)]));
			var saved = new ReviewSnapshot(metadata.Root, [.. files],
				[.. history.Where(entry => entry.Undo).OrderBy(entry => entry.Id).Select(entry => actions[entry.Id])],
				[.. history.Where(entry => !entry.Undo).OrderBy(entry => entry.Id).Select(entry => actions[entry.Id])],
				metadata.Review, metadata.NextOriginId, metadata.NextActionId, prompts);

			if (!PathIdentity.Equals(saved.Root, _workspaceRoot) || saved.Files is null || saved.Undo is null || saved.Redo is null || saved.Prompts is null
				|| saved.Review is { } review && !PathIdentity.Equals(review.Worktree, _workspaceRoot))
				throw new JsonException("The review does not belong to this worktree.");
			foreach (var file in saved.Files) {
				if (file is null || !file.Tracked || file.Path is null || !Path.IsPathFullyQualified(file.Path) || !_isInScope(file.Path)
					|| file.Baseline is null || file.Current is null || file.ReviewBaseline is null || file.AcceptedAnchor is null
					|| file.PreEdit is null || file.Disk is null)
					throw new JsonException("The review contains an out-of-scope file.");
				if (file.Provenance is { } provenance && (provenance.Text is null || provenance.Lines.IsDefault
					|| provenance.Lines.Length != LineDiff.SplitLines(provenance.Text).Length
					|| !ValidGaps(provenance.DeletedAtGap, provenance.Lines.Length)))
					throw new JsonException("The review contains invalid file provenance.");
			}
			var paths = saved.Files.Select(file => file.Path).ToHashSet(PathIdentity.Comparer);
			foreach (var action in saved.Undo.Concat(saved.Redo)) {
				if (action is null || !Enum.IsDefined(action.Kind) || action.Patches is null || action.PatchCount == 0
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
				_historyHeads[file.Path] = CaptureHistoryHead(file.Path);
			}
			foreach (var entry in history.OrderBy(entry => entry.Id))
				(entry.Undo ? _undoStack : _redoStack).Add(actions[entry.Id], entry.Id);
			_nextHistoryId = metadata.NextHistoryId;
			_review = saved.Review;
			_nextOriginId = saved.NextOriginId;
			_nextActionId = saved.NextActionId;
			foreach (var (conversation, prompt) in saved.Prompts) _conversationPrompts.Add(conversation, prompt);
			_savedMetadata = records["metadata"];
			_dirtyFiles.Clear();
			_historyDirty.Clear();
			_dirtyHistory.Clear();
			_dirtyHistoryPatches.Clear();
			_dirtyPrompts.Clear();
			ReconcileReviewDisk();
		} catch (Exception error) when (error is JsonException or ArgumentException or KeyNotFoundException) {
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
				if (_provenance.TryGetValue(path, out var provenance)) _provenance[path] = RebaseProvenance(provenance, string.Empty, []);
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

	private static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value) ?? throw new JsonException("Empty review record.");
	private sealed record ReviewMetadata(string Root, ReviewContext? Review, long NextOriginId, long NextActionId, long NextHistoryId);

	private sealed record ReviewSnapshot(string Root, PathState[] Files, List<ReviewAction> Undo,
		List<ReviewAction> Redo, ReviewContext? Review, long NextOriginId, long NextActionId, Dictionary<string, string?> Prompts);
}
