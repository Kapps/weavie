using System.Collections;
using System.Collections.Immutable;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Changes;

public sealed partial class SessionChangeTracker {
	private readonly Dictionary<ReviewAction, HistoryEntry> _historyEntries = new(ReferenceEqualityComparer.Instance);
	private readonly Dictionary<string, HashSet<ReviewAction>> _historyByPath = new(PathIdentity.Comparer);
	private readonly Dictionary<long, HistoryEntry?> _dirtyHistory = [];
	private long _nextHistoryId;
	private readonly Dictionary<string, StoredPatches?> _dirtyHistoryPatches = new(StringComparer.Ordinal);
	private static string PatchesKey(long id, string path) => $"history-patches:{id}:{FileKey(path)}";

	private IEnumerable<ReviewAction> HistoryFor(string path) =>
		_historyByPath.TryGetValue(path, out var actions) ? actions.ToArray() : [];

	private void IndexHistory(ReviewAction action, string? path) {
		var entry = _historyEntries[action];
		if (path is null) { _dirtyHistory[entry.Id] = entry; return; }
		var patches = action.PatchesFor(path);
		if (patches.Count > 0) {
			if (!_historyByPath.TryGetValue(path, out var actions)) _historyByPath[path] = actions = new(ReferenceEqualityComparer.Instance);
			actions.Add(action);
		} else if (_historyByPath.TryGetValue(path, out var actions)) {
			actions.Remove(action);
			if (actions.Count == 0) _historyByPath.Remove(path);
		}
		_dirtyHistoryPatches[PatchesKey(entry.Id, path)] = patches.Count == 0 ? null : new(entry.Id, path, patches);
	}

	private sealed class HistoryEntry(long id, bool undo, ReviewAction action) {
		public long Id { get; } = id;
		public bool Undo { get; } = undo;
		public ReviewAction Action { get; } = action;
	}

	private sealed class HistoryStack(SessionChangeTracker owner, bool undo) : IEnumerable<ReviewAction> {
		private readonly List<ReviewAction> _actions = [];
		public int Count => _actions.Count;
		public bool Exists(Predicate<ReviewAction> predicate) => _actions.Exists(predicate);
		public bool Contains(ReviewAction action) => _actions.Contains(action);
		public void Add(ReviewAction action) => Add(action, ++owner._nextHistoryId);
		public void Add(ReviewAction action, long id) {
			_actions.Add(action);
			owner._historyEntries.Add(action, new(id, undo, action));
			action.Changed += owner.IndexHistory;
			owner.IndexHistory(action, null);
			foreach (string path in action.Paths) owner.IndexHistory(action, path);
		}
		public void Remove(ReviewAction action) {
			if (!_actions.Remove(action)) return;
			action.Changed -= owner.IndexHistory;
			var entry = owner._historyEntries[action];
			foreach (string path in action.Paths) {
				var actions = owner._historyByPath[path];
				actions.Remove(action);
				if (actions.Count == 0) owner._historyByPath.Remove(path);
				owner._dirtyHistoryPatches[PatchesKey(entry.Id, path)] = null;
			}
			owner._historyEntries.Remove(action);
			owner._dirtyHistory[entry.Id] = null;
		}
		public void Clear() { foreach (var action in _actions.ToArray()) Remove(action); }
		public IEnumerator<ReviewAction> GetEnumerator() => _actions.GetEnumerator();
		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private sealed record StoredHistory(long Id, bool Undo, long ActionId, ReviewActionKind Kind, bool TouchesDisk, int? Line);
	private sealed record StoredPatches(long HistoryId, string Path, ImmutableList<ReviewPatch> Patches);
}
