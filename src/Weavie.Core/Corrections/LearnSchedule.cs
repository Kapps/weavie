using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Corrections;

/// <summary>Why a corrections analysis may not start right now.</summary>
public enum LearnRefusal {
	/// <summary>It may: the caller now holds the run slot and owes a <see cref="LearnSchedule.Release"/>.</summary>
	None,

	/// <summary>An analysis is already running for this workspace.</summary>
	Running,

	/// <summary>An analysis completed less than <see cref="LearnSchedule.Interval"/> ago.</summary>
	Cooldown,
}

/// <summary>
/// Paces one workspace's correction analyses — at most one in flight, at most one completed run per
/// <see cref="Interval"/> — and keeps the last one's rendered result. Both persist to
/// <c>~/.weavie/workspaces/&lt;id&gt;/learn.json</c>, so the limit and the result survive a restart. Claiming,
/// refusing, and <see cref="Ready"/> read the same state, so a nudge keyed off <see cref="Ready"/> can never
/// offer a run <see cref="Claim"/> would turn down. Keeping the result here is what stops a refusal being a dead
/// end: the ring that produced it is already consumed, so this is its only copy.
/// </summary>
public sealed class LearnSchedule {
	/// <summary>The minimum wall time between two completed analyses.</summary>
	public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	private readonly IFileSystem _fileSystem;
	private readonly TimeProvider _time;
	private readonly Lock _gate = new();
	private DateTimeOffset _lastRun;
	private string? _lastResult;
	private bool _running;

	/// <summary>Creates the schedule over <paramref name="path"/>, loading any persisted stamp now.</summary>
	/// <param name="fileSystem">The filesystem the stamp persists through.</param>
	/// <param name="path">The backing JSON file.</param>
	/// <param name="time">The clock the interval is measured against.</param>
	public LearnSchedule(IFileSystem fileSystem, string path, TimeProvider time) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrEmpty(path);
		ArgumentNullException.ThrowIfNull(time);
		_fileSystem = fileSystem;
		_time = time;
		FilePath = path;
		lock (_gate) {
			(_lastRun, _lastResult) = LoadLocked();
		}
	}

	/// <summary>Diagnostic log line — read/persist failures on the stamp.</summary>
	public event Action<string>? Log;

	/// <summary>Raised after a run started or ended, so a nudge keyed off <see cref="Ready"/> re-evaluates.</summary>
	public event Action? Changed;

	/// <summary>The file backing this stamp.</summary>
	public string FilePath { get; }

	/// <summary>Whether an analysis may start right now — nothing running and the interval elapsed.</summary>
	public bool Ready {
		get {
			lock (_gate) {
				return !_running && CooldownLocked() == TimeSpan.Zero;
			}
		}
	}

	/// <summary>The last completed analysis's rendered result, or <see langword="null"/> when there is none.</summary>
	public string? LastResult {
		get {
			lock (_gate) {
				return _lastResult;
			}
		}
	}

	/// <summary>
	/// Claims the single analysis slot, or reports why one may not start now. A <see cref="LearnRefusal.None"/>
	/// result means the caller holds the slot and must match it with a <see cref="Release"/>.
	/// </summary>
	/// <param name="message">The user-facing reason the claim failed; empty when it succeeded.</param>
	public LearnRefusal Claim(out string message) {
		lock (_gate) {
			if (_running) {
				message = "Your corrections are already being analyzed.";
				return LearnRefusal.Running;
			}

			var cooldown = CooldownLocked();
			if (cooldown > TimeSpan.Zero) {
				message = $"Weavie analyzes your corrections at most once every {Interval.TotalHours:0} hours — "
					+ $"the next analysis is available in {Describe(cooldown)}.";
				return LearnRefusal.Cooldown;
			}

			_running = true;
			message = string.Empty;
		}

		Changed?.Invoke();
		return LearnRefusal.None;
	}

	/// <summary>
	/// Releases the slot a <see cref="Claim"/> took. A <paramref name="result"/> starts a fresh interval and
	/// becomes the kept <see cref="LastResult"/>; <see langword="null"/> — a failure, or a run that never began —
	/// leaves both untouched, so a failure never costs the user a day.
	/// </summary>
	/// <param name="result">The completed analysis's rendered result, or null when there was none.</param>
	public void Release(string? result) {
		lock (_gate) {
			_running = false;
			if (result is not null) {
				_lastRun = _time.GetUtcNow();
				_lastResult = result;
				PersistLocked();
			}
		}

		Changed?.Invoke();
	}

	private TimeSpan CooldownLocked() {
		if (_lastRun == default) {
			return TimeSpan.Zero;
		}

		var remaining = _lastRun + Interval - _time.GetUtcNow();
		return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
	}

	// "23 hours" / "40 minutes" / "under a minute" — the granularity a daily limit is actually read at.
	private static string Describe(TimeSpan remaining) {
		if (remaining >= TimeSpan.FromHours(1)) {
			int hours = (int)Math.Ceiling(remaining.TotalHours);
			return $"{hours.ToString(CultureInfo.InvariantCulture)} hour{(hours == 1 ? string.Empty : "s")}";
		}

		if (remaining >= TimeSpan.FromMinutes(1)) {
			int minutes = (int)Math.Ceiling(remaining.TotalMinutes);
			return $"{minutes.ToString(CultureInfo.InvariantCulture)} minute{(minutes == 1 ? string.Empty : "s")}";
		}

		return "under a minute";
	}

	private (DateTimeOffset LastRun, string? LastResult) LoadLocked() =>
		JsonStoreFile.Load(
			_fileSystem,
			FilePath,
			static text => JsonSerializer.Deserialize<LearnDocument>(text) is { } document
				? (document.LastRunUtc, document.LastResult)
				: default,
			static () => default,
			Log);

	private void PersistLocked() =>
		JsonStoreFile.Persist(
			_fileSystem,
			FilePath,
			JsonSerializer.Serialize(
				new LearnDocument { Version = 1, LastRunUtc = _lastRun, LastResult = _lastResult },
				JsonOptions),
			Log);

	private sealed class LearnDocument {
		[JsonPropertyName("version")]
		public int Version { get; set; }

		[JsonPropertyName("lastRunUtc")]
		public DateTimeOffset LastRunUtc { get; set; }

		[JsonPropertyName("lastResult")]
		public string? LastResult { get; set; }
	}
}

/// <summary>
/// What the corrections nudge reads each evaluation: how many corrections are waiting and whether one may be
/// analyzed right now.
/// </summary>
/// <param name="Pending">The correction ring's live entry count.</param>
/// <param name="Ready">Whether an analysis may start right now (see <see cref="LearnSchedule.Ready"/>).</param>
public readonly record struct CorrectionsStatus(int Pending, bool Ready);
