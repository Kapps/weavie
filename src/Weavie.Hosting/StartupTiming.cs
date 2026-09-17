using System.Diagnostics;
using System.Globalization;

namespace Weavie.Hosting;

internal sealed class StartupTiming(bool enabled, string workspace, Action<string> log) {
	private readonly long _started = Stopwatch.GetTimestamp();

	public bool Enabled { get; } = enabled;

	public IDisposable Measure(string phase) => Enabled ? new Phase(this, phase) : DisabledPhase.Instance;

	public void Mark(string phase) {
		if (Enabled) Write(phase);
	}

	private void Write(string message) => log(string.Create(CultureInfo.InvariantCulture,
		$"[startup/host workspace={workspace}] +{Stopwatch.GetElapsedTime(_started).TotalMilliseconds:F0}ms {message}"));

	private sealed class Phase : IDisposable {
		private readonly StartupTiming _owner;
		private readonly string _name;
		private readonly long _started = Stopwatch.GetTimestamp();

		public Phase(StartupTiming owner, string name) {
			_owner = owner;
			_name = name;
			_owner.Write($"begin {name}");
		}

		public void Dispose() => _owner.Write(string.Create(CultureInfo.InvariantCulture,
			$"end {_name}: {Stopwatch.GetElapsedTime(_started).TotalMilliseconds:F0}ms"));
	}

	private sealed class DisabledPhase : IDisposable {
		public static DisabledPhase Instance { get; } = new();
		public void Dispose() { }
	}
}
