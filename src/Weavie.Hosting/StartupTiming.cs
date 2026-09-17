using System.Diagnostics;
using System.Globalization;

namespace Weavie.Hosting;

internal sealed class StartupTiming(string workspace, Action<string> log) {
	private readonly long _started = Stopwatch.GetTimestamp();

	public IDisposable Measure(string phase) => new Phase(this, phase);

	public void Mark(string phase) => Write(phase);

	private void Write(string message) => log(string.Create(CultureInfo.InvariantCulture,
		$"[startup/host] +{Stopwatch.GetElapsedTime(_started).TotalMilliseconds:F0}ms {message} (workspace={workspace})"));

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

}
