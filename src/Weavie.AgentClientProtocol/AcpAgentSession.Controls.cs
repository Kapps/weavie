using System.Text.Json;
using Weavie.Core.Agents;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	/// <inheritdoc/>
	public void SetControl(string axis, string value) {
		ArgumentException.ThrowIfNullOrEmpty(axis);
		ArgumentException.ThrowIfNullOrEmpty(value);
		lock (_gate) {
			if (!_controls.TryGetValue(axis, out var control)) {
				EmitFailure(new AcpProtocolException($"ACP did not advertise the '{axis}' control."));
				return;
			}
			if (control.Options.All(option => !string.Equals(option.Id, value, StringComparison.Ordinal))) {
				EmitFailure(new AcpProtocolException($"ACP did not advertise '{value}' for the '{axis}' control."));
				return;
			}
			if (!_ready || _sessionId is null) throw new InvalidOperationException("The ACP session is not ready.");
			_controlMutations.Enqueue(new AcpControlMutation(axis, value));
		}
		DispatchControlMutation();
	}

	private void DispatchControlMutation() {
		AcpControlMutation mutation;
		long generation;
		lock (_gate) {
			if (_controlMutationActive || _controlMutations.Count == 0 || !_ready || _disposed || _runtimeFailed) {
				return;
			}
			_controlMutationActive = true;
			mutation = _controlMutations.Dequeue();
			generation = _activeGeneration;
		}
		_ = Task.Run(async () => {
			try {
				await DeliverControlMutationAsync(mutation, generation, persist: true).ConfigureAwait(false);
			} catch (Exception ex) when (ex is not OperationCanceledException) {
				lock (_turnTransitionGate) {
					if (OwnsGeneration(generation)) {
						if (ex is IOException or AcpProtocolException) FailRuntimeSerialized(ex);
						else EmitFailure(ex);
					}
				}
			} finally {
				lock (_gate) {
					if (_activeGeneration == generation) _controlMutationActive = false;
				}
				DispatchControlMutation();
			}
		});
	}

	private async Task DeliverControlMutationAsync(AcpControlMutation mutation, long generation, bool persist) {
		AgentControlAxis control;
		string sessionId;
		bool mode;
		lock (_gate) {
			if (_sessionId is null) return;
			if (!_controls.TryGetValue(mutation.Axis, out control!)
				|| control.Options.All(option => option.Id != mutation.Value)) {
				throw new AcpProtocolException(
					$"ACP no longer advertises '{mutation.Value}' for the '{mutation.Axis}' control.");
			}
			sessionId = _sessionId;
			mode = mutation.Axis == "mode" && !_configOwnsMode;
		}

		JsonElement result;
		if (mode) {
			result = await _connection.RequestAsync(
				"session/set_mode",
				new { sessionId, modeId = mutation.Value },
				generation,
				CancellationToken.None).ConfigureAwait(false);
		} else {
			result = await _connection.RequestAsync(
				"session/set_config_option",
				AcpConfigurationOptions.SetParameters(sessionId, control, mutation.Value),
				generation,
				CancellationToken.None).ConfigureAwait(false);
		}
		lock (_turnTransitionGate) {
			lock (_gate) {
				if (_disposed || _activeGeneration != generation) return;
				if (mode) _controls[mutation.Axis] = WithValue(control, mutation.Value);
				else ReadControlResultLocked(result);
				if (persist) _controlDefaults.Set(_definition.Id, mutation.Axis, _controls[mutation.Axis].Value);
			}
			RaiseControls();
		}
	}

	private async Task RestoreControlDefaultsAsync(long generation) {
		var pending = new Dictionary<string, string>(_controlDefaults.Resolve(_definition.Id), StringComparer.Ordinal);
		while (pending.Count > 0) {
			AcpControlMutation? mutation = null;
			bool consumed = false;
			string? stale = null;
			lock (_gate) {
				foreach (var control in _controls.Values) {
					if (!pending.Remove(control.Id, out string? value)) continue;
					consumed = true;
					if (control.Options.All(option => option.Id != value)) {
						_controlDefaults.Clear(_definition.Id, control.Id, value);
						stale = $"Saved {_definition.Name} control '{control.Id}' value '{value}' is no longer advertised and was forgotten.";
						break;
					}
					if (control.Value != value) mutation = new AcpControlMutation(control.Id, value);
					break;
				}
			}
			if (stale is not null) {
				EmitFailure(new AcpProtocolException(stale));
				continue;
			}
			if (mutation is null) {
				if (consumed) continue;
				break;
			}
			await DeliverControlMutationAsync(mutation, generation, persist: false).ConfigureAwait(false);
		}
	}

	private void ReadControlResultLocked(JsonElement result) {
		ReplaceConfigOptionsLocked(AcpConfigurationOptions.ReadRequired(
			result, "The ACP configuration response is missing configOptions."));
	}

	private void ReadControlStateLocked(JsonElement setup) {
		_controls.Clear();
		foreach (var control in AcpConfigurationOptions.ReadIfPresent(setup)) _controls.Add(control.Id, control);
		_configOwnsMode = _controls.ContainsKey("mode");
		if (setup.TryGetProperty("modes", out var modes)) {
			if (modes.ValueKind == JsonValueKind.Null) return;
			if (modes.ValueKind != JsonValueKind.Object) {
				throw new AcpProtocolException("ACP modes must be an object when present.");
			}
			ReadModesLocked(modes);
		}
	}

	private void UpdateConfig(JsonElement update) {
		var config = AcpConfigurationOptions.ReadRequired(
			update, "An ACP config update is missing configOptions.");
		lock (_gate) {
			ReplaceConfigOptionsLocked(config);
		}
		RaiseControls();
	}

	private void ReplaceConfigOptionsLocked(IReadOnlyList<AgentControlAxis> config) {
		var legacyMode = _configOwnsMode ? null : _controls.GetValueOrDefault("mode");
		_controls.Clear();
		foreach (var control in config) _controls.Add(control.Id, control);
		_configOwnsMode = _controls.ContainsKey("mode");
		if (!_configOwnsMode && legacyMode is not null) _controls.Add("mode", legacyMode);
	}

	private void UpdateMode(JsonElement update) {
		string value = RequiredString(update, "currentModeId", "current mode update");
		lock (_gate) {
			if (_controls.TryGetValue("mode", out var mode)) {
				if (mode.Options.All(option => option.Id != value)) {
					throw new AcpProtocolException($"ACP current mode update selected unadvertised mode '{value}'.");
				}
				_controls["mode"] = WithValue(mode, value);
			}
		}
		RaiseControls();
	}

	private void UpdateCommands(JsonElement update) {
		if (!update.TryGetProperty("availableCommands", out var commands) || commands.ValueKind != JsonValueKind.Array) {
			throw new AcpProtocolException("An ACP command update is missing availableCommands.");
		}
		var parsed = commands.EnumerateArray().Select(command => {
			string name = RequiredString(command, "name", "available command");
			return new AgentSlashEntry {
				Id = "agent:" + name,
				Name = name,
				Description = RequiredString(command, "description", "available command"),
				Kind = AgentSlashEntryKind.ProviderCommand,
				InputHint = ReadCommandInput(command, name),
			};
		}).ToArray();
		if (parsed.Select(command => command.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != parsed.Length) {
			throw new AcpProtocolException("ACP available commands repeat a name.");
		}
		lock (_gate) {
			_commands = [.. parsed.Where(command =>
				!string.Equals(command.Name, "clear", StringComparison.OrdinalIgnoreCase))];
		}
		RaiseControls();
	}

	private static string? ReadCommandInput(JsonElement command, string name) {
		if (!command.TryGetProperty("input", out var input) || input.ValueKind == JsonValueKind.Null) {
			return null;
		}
		if (input.ValueKind != JsonValueKind.Object) {
			throw new AcpProtocolException($"ACP available command '{name}' has invalid input metadata.");
		}
		return RequiredString(input, "hint", $"available command '{name}' input");
	}

	// Agents mirror one mode axis in both configOptions and the legacy modes block. The config option owns
	// it, because session/set_config_option is what writes it back.
	private void ReadModesLocked(JsonElement modes) {
		if (_configOwnsMode) return;
		string current = RequiredString(modes, "currentModeId", "session mode state");
		if (!modes.TryGetProperty("availableModes", out var available) || available.ValueKind != JsonValueKind.Array) {
			throw new AcpProtocolException("The ACP session mode state is missing availableModes.");
		}
		var options = available.EnumerateArray().Select(mode => new AgentControlOption {
			Id = RequiredString(mode, "id", "session mode"),
			Label = RequiredString(mode, "name", "session mode"),
			Description = OptionalString(mode, "description"),
		}).ToArray();
		if (options.Select(option => option.Id).Distinct(StringComparer.Ordinal).Count() != options.Length) {
			throw new AcpProtocolException("ACP session modes repeat an id.");
		}
		if (options.All(option => option.Id != current)) {
			throw new AcpProtocolException($"ACP session modes omit currentModeId '{current}'.");
		}
		_controls["mode"] = new AgentControlAxis {
			Id = "mode",
			Label = "Mode",
			Category = "mode",
			Kind = "select",
			Value = current,
			ValueLabel = options.FirstOrDefault(option => option.Id == current)?.Label ?? current,
			Options = options,
		};
	}

	private static AgentControlAxis WithValue(AgentControlAxis control, string value) => control with {
		Value = value,
		ValueLabel = control.Options.FirstOrDefault(option => option.Id == value)?.Label ?? value,
	};

	private void RaiseControls() => ControlStateChanged?.Invoke(ControlState);
}
