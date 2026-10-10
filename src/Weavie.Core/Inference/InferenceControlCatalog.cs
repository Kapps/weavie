using Weavie.Core.Agents;
using Weavie.Core.Configuration;

namespace Weavie.Core.Inference;

/// <summary>
/// The one owner of what the suggestion profile can be set to. Asks the selected inference provider once opened and
/// again whenever the provider, the model, or the provider catalog changes; nothing refreshes on a timer.
/// </summary>
public sealed class InferenceControlCatalog : IDisposable {
	private readonly SettingsStore _settings;
	private readonly AgentProviderRegistry _providers;
	private readonly Lock _gate = new();
	private bool _opened;
	private bool _disposed;
	private long _version;
	// Unlinked and timer-free, so it needs no disposal.
	private CancellationTokenSource? _probe;
	private InferenceControlsStatus _status = InferenceControlsStatus.Probing;
	private string? _error;
	private (string Provider, string Model) _asked = ("", "");
	private (string Provider, InferenceControls Controls)? _answer;

	/// <summary>Creates the catalog over live settings and the installed providers; nothing runs until <see cref="Open"/>.</summary>
	public InferenceControlCatalog(SettingsStore settings, AgentProviderRegistry providers) {
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(providers);
		_settings = settings;
		_providers = providers;
		_settings.SettingChanged += OnSettingChanged;
		_providers.Changed += Refresh;
	}

	/// <summary>Raised after <see cref="State"/> changes.</summary>
	public event Action? Changed;

	/// <summary>The current pickers.</summary>
	public InferenceControlsState State {
		get {
			lock (_gate) {
				string provider = _settings.RequireString(InferenceSettings.DefaultProvider);
				var controls = _answer is { } answer && answer.Provider == provider ? answer.Controls : null;
				return InferenceControlAxes.Build(_settings, _providers, _status, _error, controls);
			}
		}
	}

	/// <summary>Returns the current pickers, asking the provider the first time.</summary>
	public InferenceControlsState Open() {
		bool first;
		lock (_gate) {
			first = !_opened;
			_opened = true;
		}
		if (first) Refresh();
		return State;
	}

	/// <summary>Asks the selected provider again, e.g. after a failure.</summary>
	public void Refresh() => Ask(force: true);

	private void Ask(bool force) {
		CancellationTokenSource? superseded;
		CancellationTokenSource probe;
		long version;
		string providerId;
		string model;
		lock (_gate) {
			if (_disposed || !_opened) return;
			providerId = _settings.RequireString(InferenceSettings.DefaultProvider);
			model = _settings.RequireString(InferenceSettings.Model);
			if (!force && _asked == (providerId, model)) return;
			_asked = (providerId, model);
			superseded = _probe;
			_probe = probe = new CancellationTokenSource();
			version = ++_version;
			_status = InferenceControlsStatus.Probing;
			_error = null;
		}
		superseded?.Cancel();
		Changed?.Invoke();
		_ = ProbeAsync(providerId, model, version, probe.Token);
	}

	private async Task ProbeAsync(string providerId, string model, long version, CancellationToken ct) {
		InferenceControls? controls = null;
		string? error = null;
		try {
			if (InferenceService.Resolve(_providers, providerId, out string unavailable) is not { } provider) {
				error = unavailable;
			} else {
				controls = await provider.ProbeInferenceControlsAsync(model, ct).ConfigureAwait(false);
			}
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			return;
		} catch (Exception ex) {
			error = ex.Message;
		}
		lock (_gate) {
			if (version != _version) return;
			_probe = null;
			_status = error is null ? InferenceControlsStatus.Ready : InferenceControlsStatus.Failed;
			_error = error;
			if (controls is not null) _answer = (providerId, controls);
		}
		Changed?.Invoke();
	}

	private void OnSettingChanged(SettingChange change) {
		if (change.Key is CoreSettings.ClaudePath) {
			Refresh();
		} else if (change.Key is InferenceSettings.DefaultProvider or InferenceSettings.Model) {
			Ask(force: false);
		} else if (change.Key is InferenceSettings.Effort or InferenceSettings.FastMode
			or InferenceSettings.Enabled or InferenceSettings.AllowAutomatic) {
			Changed?.Invoke();
		}
	}

	/// <inheritdoc/>
	public void Dispose() {
		_settings.SettingChanged -= OnSettingChanged;
		_providers.Changed -= Refresh;
		CancellationTokenSource? probe;
		lock (_gate) {
			_disposed = true;
			probe = _probe;
			_probe = null;
		}
		probe?.Cancel();
	}
}
