using System.Runtime.CompilerServices;

namespace Weavie.Hosting;

internal sealed class SpellOperationRegistry {
	private readonly object _gate = new();
	private readonly Dictionary<OperationKey, Operation> _current = [];
	private readonly HashSet<Operation> _live = [];
	private readonly ConditionalWeakTable<HostSession, StopMarker> _stoppedSessions = [];
	private bool _stopped;

	private sealed class StopMarker {
	}

	internal sealed class Operation : IDisposable {
		private readonly CancellationTokenSource _cancellation = new();
		private readonly TaskCompletionSource<bool> _completion =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		internal Operation(OperationKey key) {
			Key = key;
		}

		internal OperationKey Key { get; }
		internal CancellationToken Token => _cancellation.Token;
		internal Task Completion => _completion.Task;

		internal void Cancel() {
			try {
				_cancellation.Cancel();
			} catch (ObjectDisposedException) {
				// Completion won the race and already released the cancellation source.
			}
		}

		internal void Complete() => _completion.TrySetResult(true);

		public void Dispose() => _cancellation.Dispose();
	}

	internal readonly record struct OperationKey(
		HostSession Session,
		string Kind,
		string Resource);

	internal Operation? Begin(HostSession session, string kind, string resource) {
		var operation = new Operation(new OperationKey(session, kind, resource));
		Operation? previous;
		lock (_gate) {
			if (_stopped || _stoppedSessions.TryGetValue(session, out _)) {
				operation.Dispose();
				return null;
			}

			_current.TryGetValue(operation.Key, out previous);
			_current[operation.Key] = operation;
			_live.Add(operation);
		}

		previous?.Cancel();
		return operation;
	}

	internal bool IsCurrent(Operation operation) {
		lock (_gate) {
			return _current.TryGetValue(operation.Key, out var current)
				&& ReferenceEquals(current, operation);
		}
	}

	internal void End(Operation operation) {
		lock (_gate) {
			if (_current.TryGetValue(operation.Key, out var current)
				&& ReferenceEquals(current, operation)) {
				_current.Remove(operation.Key);
			}

			_live.Remove(operation);
		}

		operation.Complete();
		operation.Dispose();
	}

	internal void CancelAll() => Cancel(SnapshotAll());

	internal void CancelSession(HostSession session) => Cancel(SnapshotSession(session));

	internal Task StopAllAsync() {
		Operation[] operations;
		lock (_gate) {
			_stopped = true;
			operations = [.. _live];
		}

		return StopAsync(operations);
	}

	internal Task StopSessionAsync(HostSession session) {
		Operation[] operations;
		lock (_gate) {
			if (!_stoppedSessions.TryGetValue(session, out _)) {
				_stoppedSessions.Add(session, new StopMarker());
			}
			operations = [.. _live.Where(operation => ReferenceEquals(operation.Key.Session, session))];
		}

		return StopAsync(operations);
	}

	private Operation[] SnapshotAll() {
		lock (_gate) {
			return [.. _live];
		}
	}

	private Operation[] SnapshotSession(HostSession session) {
		lock (_gate) {
			return [.. _live.Where(operation => ReferenceEquals(operation.Key.Session, session))];
		}
	}

	private static void Cancel(IEnumerable<Operation> operations) {
		foreach (var operation in operations) {
			operation.Cancel();
		}
	}

	private static Task StopAsync(Operation[] operations) {
		Cancel(operations);
		return Task.WhenAll(operations.Select(operation => operation.Completion));
	}

	internal int LiveCount {
		get {
			lock (_gate) {
				return _live.Count;
			}
		}
	}
}

internal sealed class QuiescingTaskRegistry<TScope> where TScope : class {
	private readonly object _gate = new();
	private readonly Dictionary<TScope, HashSet<Entry>> _live =
		new(ReferenceEqualityComparer.Instance);
	private readonly ConditionalWeakTable<TScope, StopMarker> _stoppedScopes = [];
	private bool _stopped;

	private sealed class StopMarker {
	}

	private sealed class Entry(TScope scope) {
		internal TScope Scope { get; } = scope;
		internal TaskCompletionSource Completion { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
	}

	internal bool TryRun(TScope scope, Func<Task> work) {
		ArgumentNullException.ThrowIfNull(scope);
		ArgumentNullException.ThrowIfNull(work);
		var entry = new Entry(scope);
		lock (_gate) {
			if (_stopped || _stoppedScopes.TryGetValue(scope, out _)) {
				return false;
			}

			if (!_live.TryGetValue(scope, out var entries)) {
				entries = [];
				_live.Add(scope, entries);
			}

			entries.Add(entry);
		}

		_ = RunAsync(entry, work);
		return true;
	}

	internal Task StopAllAsync() {
		Entry[] entries;
		lock (_gate) {
			_stopped = true;
			entries = [.. _live.Values.SelectMany(static values => values)];
		}

		return Task.WhenAll(entries.Select(static entry => entry.Completion.Task));
	}

	internal Task StopScopeAsync(TScope scope) {
		Entry[] entries;
		lock (_gate) {
			if (!_stoppedScopes.TryGetValue(scope, out _)) {
				_stoppedScopes.Add(scope, new StopMarker());
			}
			entries = _live.TryGetValue(scope, out var current) ? [.. current] : [];
		}

		return Task.WhenAll(entries.Select(static entry => entry.Completion.Task));
	}

	private async Task RunAsync(Entry entry, Func<Task> work) {
		Exception? error = null;
		try {
			await work().ConfigureAwait(false);
		} catch (Exception ex) {
			error = ex;
		} finally {
			lock (_gate) {
				if (_live.TryGetValue(entry.Scope, out var entries)) {
					entries.Remove(entry);
					if (entries.Count == 0) {
						_live.Remove(entry.Scope);
					}
				}
			}

			if (error is null) {
				entry.Completion.TrySetResult();
			} else {
				entry.Completion.TrySetException(error);
			}
		}
	}
}

public sealed partial class HostCore {
	private readonly SpellOperationRegistry _spellOperationRegistry = new();
	private readonly QuiescingTaskRegistry<HostSession> _spellDictionaryWrites = new();

	internal int PendingSpellOperationCountForTest => _spellOperationRegistry.LiveCount;

	private void CancelAllSpellOperations() => _spellOperationRegistry.CancelAll();

	private void CancelSpellOperationsForSession(HostSession? session) {
		if (session is not null) {
			_spellOperationRegistry.CancelSession(session);
		}
	}

	private Task StopAllSpellOperationsAsync() => _spellOperationRegistry.StopAllAsync();

	private Task StopSpellOperationsForSessionAsync(HostSession session) =>
		_spellOperationRegistry.StopSessionAsync(session);

	private Task StopAllSpellDictionaryWritesAsync() => _spellDictionaryWrites.StopAllAsync();

	private Task StopSpellDictionaryWritesForSessionAsync(HostSession session) =>
		_spellDictionaryWrites.StopScopeAsync(session);
}
