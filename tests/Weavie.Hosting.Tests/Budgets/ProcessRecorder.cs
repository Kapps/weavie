using System.Diagnostics;
using Weavie.Core.FileSystem;
using Weavie.Core.Processes;

namespace Weavie.Hosting.Tests;

/// <summary>Records every captured process started in one directory tree, from start until disposal.</summary>
internal sealed class ProcessRecorder : IDisposable {
	private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(500);
	private readonly string _root;
	private readonly ActivityListener _listener;
	private readonly Lock _gate = new();
	private readonly List<string> _started = [];
	private readonly HashSet<Activity> _running = [];
	private long _lastChange = Stopwatch.GetTimestamp();

	public ProcessRecorder(string root) {
		_root = Path.GetFullPath(root);
		_listener = new ActivityListener {
			ShouldListenTo = source => source == ProcessCapture.Activities,
			Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
			ActivityStarted = activity => Track(activity, started: true),
			ActivityStopped = activity => Track(activity, started: false),
		};
		ActivitySource.AddActivityListener(_listener);
	}

	/// <summary>Waits until a process whose "executable arguments" contain <paramref name="text"/> has started.</summary>
	public Task StartedAsync(string text) => Wait.UntilAsync(() => {
		lock (_gate) return _started.Any(started => started.Contains(text, StringComparison.Ordinal));
	}, TimeSpan.FromMinutes(1));

	/// <summary>Each process started since the last <see cref="Take"/>, as "executable arguments".</summary>
	public IReadOnlyList<string> Take() {
		lock (_gate) {
			string[] started = [.. _started];
			_started.Clear();
			return started;
		}
	}

	/// <summary>
	/// Waits until nothing in the tree is running and nothing has started or stopped for a moment since this call,
	/// so work an operation queued but hasn't begun yet still lands inside its window.
	/// </summary>
	public Task SettleAsync() {
		lock (_gate) _lastChange = Stopwatch.GetTimestamp();
		return Wait.UntilAsync(() => {
			lock (_gate) return _running.Count == 0 && Stopwatch.GetElapsedTime(_lastChange) >= Quiet;
		}, TimeSpan.FromMinutes(1));
	}

	// A run already going when recording began finishes untracked; only runs seen starting count.
	private void Track(Activity activity, bool started) {
		if (activity.GetTagItem("process.working_directory") is not string directory
			|| !PathBoundary.Contains(_root, directory)) return;
		lock (_gate) {
			if (started ? !_running.Add(activity) : !_running.Remove(activity)) return;
			_lastChange = Stopwatch.GetTimestamp();
			if (started) _started.Add($"{activity.OperationName} {activity.GetTagItem("process.arguments")}");
		}
	}

	public void Dispose() => _listener.Dispose();
}
