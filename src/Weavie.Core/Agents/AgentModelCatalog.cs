namespace Weavie.Core.Agents;

/// <summary>Where a model snapshot came from.</summary>
public enum AgentModelSource {
	/// <summary>A background throwaway session.</summary>
	Probe,

	/// <summary>A live session of that provider.</summary>
	Session,

	/// <summary>A consult turn.</summary>
	Consult,
}

/// <summary>The state of one provider's model snapshot.</summary>
public enum AgentModelStatus {
	/// <summary>A probe is running and no snapshot has landed since it started.</summary>
	Probing,

	/// <summary><see cref="AgentModelEntry.Models"/> is the latest observation.</summary>
	Ready,

	/// <summary>The latest refresh failed; <see cref="AgentModelEntry.Error"/> says why.</summary>
	Failed,
}

/// <summary>One consultable provider's latest model snapshot.</summary>
public sealed record AgentModelEntry {
	/// <summary>The snapshot state.</summary>
	public required AgentModelStatus Status { get; init; }

	/// <summary>The advertised model options when ready; empty when the agent has no model selector.</summary>
	public required IReadOnlyList<AgentControlOption> Models { get; init; }

	/// <summary>Where a ready snapshot came from.</summary>
	public required AgentModelSource Source { get; init; }

	/// <summary>The failure detail when <see cref="Status"/> is <see cref="AgentModelStatus.Failed"/>.</summary>
	public required string? Error { get; init; }

	/// <summary>When this state began.</summary>
	public required DateTimeOffset At { get; init; }
}

/// <summary>
/// The one owner of each consultable provider's advertised models. Snapshots are replaced whole, a failed refresh
/// replaces what it refreshed, and nothing refreshes on a timer. See docs/specs/agent-consultation.md.
/// </summary>
public sealed class AgentModelCatalog : IDisposable {
	private readonly AgentProviderRegistry _providers;
	private readonly Lock _gate = new();
	private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
	private long _sequence;
	private bool _disposed;

	/// <summary>Creates the catalog over <paramref name="providers"/>; call <see cref="Start"/> to begin probing.</summary>
	public AgentModelCatalog(AgentProviderRegistry providers) {
		ArgumentNullException.ThrowIfNull(providers);
		_providers = providers;
	}

	/// <summary>Probes every consultable provider now and again whenever the provider catalog changes.</summary>
	public void Start() {
		_providers.Changed += Refresh;
		Refresh();
	}

	/// <summary>The latest snapshot for <paramref name="providerId"/>, or <c>null</c> when it isn't consultable.</summary>
	public AgentModelEntry? Find(string providerId) {
		lock (_gate) return _slots.TryGetValue(providerId, out var slot) ? slot.Entry : null;
	}

	/// <summary>Replaces <paramref name="providerId"/>'s snapshot with controls a live session advertised.</summary>
	public void Observe(string providerId, IReadOnlyList<AgentControlAxis> controls, AgentModelSource source) {
		ArgumentNullException.ThrowIfNull(controls);
		lock (_gate) {
			if (!_slots.TryGetValue(providerId, out var slot)) return;
			slot.Version = ++_sequence;
			slot.Entry = Ready(controls, source);
		}
	}

	private void Refresh() {
		var probes = new List<(IAgentConsultProvider Provider, Slot Slot, long Version, CancellationTokenSource Probe)>();
		CancellationTokenSource[] superseded;
		lock (_gate) {
			if (_disposed) return;
			superseded = TakeProbes();
			foreach (var provider in _providers.Providers.OfType<IAgentConsultProvider>().Where(p => p.Info.Available)) {
				var slot = new Slot {
					Probe = new CancellationTokenSource(),
					Version = ++_sequence,
					Entry = State(AgentModelStatus.Probing, [], null),
				};
				_slots[provider.Info.Id] = slot;
				probes.Add((provider, slot, slot.Version, slot.Probe));
			}
		}
		foreach (var probe in superseded) probe.Cancel();
		foreach (var (provider, slot, version, probe) in probes) _ = ProbeAsync(provider, slot, version, probe);
	}

	// Callers cancel the taken probes after leaving the lock: a probe may complete inline on cancellation.
	private CancellationTokenSource[] TakeProbes() {
		var probes = _slots.Values.Select(slot => slot.Probe).OfType<CancellationTokenSource>().ToArray();
		_slots.Clear();
		return probes;
	}

	private async Task ProbeAsync(IAgentConsultProvider provider, Slot slot, long version, CancellationTokenSource probe) {
		AgentModelEntry? entry;
		try {
			entry = Ready(await provider.ProbeControlsAsync(probe.Token).ConfigureAwait(false), AgentModelSource.Probe);
		} catch (OperationCanceledException) when (probe.IsCancellationRequested) {
			entry = null;
		} catch (Exception ex) {
			entry = State(AgentModelStatus.Failed, [], ex.Message);
		}
		lock (_gate) {
			slot.Probe = null;
			// A newer observation or a refresh supersedes this probe's result.
			if (entry is not null && slot.Version == version && _slots.ContainsValue(slot)) slot.Entry = entry;
		}
	}

	private static AgentModelEntry Ready(IReadOnlyList<AgentControlAxis> controls, AgentModelSource source) =>
		State(AgentModelStatus.Ready, controls.FirstOrDefault(c => c.Category == "model")?.Options ?? [], null) with {
			Source = source,
		};

	private static AgentModelEntry State(AgentModelStatus status, IReadOnlyList<AgentControlOption> models, string? error) =>
		new() { Status = status, Models = models, Source = AgentModelSource.Probe, Error = error, At = DateTimeOffset.UtcNow };

	/// <inheritdoc/>
	public void Dispose() {
		_providers.Changed -= Refresh;
		CancellationTokenSource[] probes;
		lock (_gate) {
			_disposed = true;
			probes = TakeProbes();
		}
		foreach (var probe in probes) probe.Cancel();
	}

	private sealed class Slot {
		// Unlinked and timer-free, so it needs no disposal; null once its probe has finished.
		public required CancellationTokenSource? Probe { get; set; }
		public required long Version { get; set; }
		public required AgentModelEntry Entry { get; set; }
	}
}
