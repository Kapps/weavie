namespace Weavie.Core.Diagnostics;

/// <summary>Runs bounded, ordered diagnostic work without letting its sink block the caller.</summary>
public sealed class DiagnosticWorker {
	private const int PendingLimit = 256;
	private readonly Action<string> _failureSink;
	private readonly object _gate = new();
	private readonly Queue<DiagnosticWork> _pending = [];
	private bool _running;
	private int _coalesced;
	private string? _latestCoalesced;

	/// <summary>Creates a worker whose failures and coalescing summaries go to <paramref name="failureSink"/>.</summary>
	public DiagnosticWorker(Action<string> failureSink) {
		ArgumentNullException.ThrowIfNull(failureSink);
		_failureSink = failureSink;
	}

	/// <summary>Reports one message through the worker's sink.</summary>
	public void Report(string message) {
		ArgumentNullException.ThrowIfNull(message);
		Run(message, () => _failureSink(message));
	}

	/// <summary>Queues one named diagnostic action.</summary>
	public void Run(string description, Action action) {
		ArgumentException.ThrowIfNullOrEmpty(description);
		ArgumentNullException.ThrowIfNull(action);
		bool start;
		lock (_gate) {
			if (_pending.Count == PendingLimit) {
				_coalesced++;
				_latestCoalesced = description;
				return;
			}

			_pending.Enqueue(new DiagnosticWork(description, action));
			start = !_running;
			_running = true;
		}

		if (start) {
			ThreadPool.UnsafeQueueUserWorkItem(static worker => worker.Drain(), this, preferLocal: false);
		}
	}

	private void Drain() {
		while (true) {
			DiagnosticWork work;
			lock (_gate) {
				if (_pending.Count > 0) {
					work = _pending.Dequeue();
				} else if (_coalesced > 0) {
					string message = $"Coalesced {_coalesced} diagnostics while the sink was blocked; "
						+ $"latest: {_latestCoalesced}.";
					_coalesced = 0;
					_latestCoalesced = null;
					work = new DiagnosticWork(message, () => _failureSink(message));
				} else {
					_running = false;
					return;
				}
			}

			try {
				work.Action();
			} catch (Exception ex) {
				try {
					_failureSink($"Diagnostic '{work.Description}' failed: {ex}");
				} catch (Exception) {
				}
			}
		}
	}

	private sealed record DiagnosticWork(string Description, Action Action);
}
