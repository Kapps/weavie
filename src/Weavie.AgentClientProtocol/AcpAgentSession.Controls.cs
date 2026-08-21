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
		} else if (control.Kind == "boolean") {
			if (!bool.TryParse(mutation.Value, out bool boolean)) {
				throw new AcpProtocolException($"'{mutation.Value}' is not a boolean ACP configuration value.");
			}
			result = await _connection.RequestAsync(
				"session/set_config_option",
				new { sessionId, configId = mutation.Axis, type = "boolean", value = boolean },
				generation,
				CancellationToken.None).ConfigureAwait(false);
		} else {
			result = await _connection.RequestAsync(
				"session/set_config_option",
				new { sessionId, configId = mutation.Axis, value = mutation.Value },
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
		if (!result.TryGetProperty("configOptions", out var options) || options.ValueKind != JsonValueKind.Array) {
			throw new AcpProtocolException("The ACP configuration response is missing configOptions.");
		}
		ReplaceConfigOptionsLocked(options);
	}

	private void ReadControlStateLocked(JsonElement setup) {
		_controls.Clear();
		if (setup.TryGetProperty("configOptions", out var config)
			&& config.ValueKind != JsonValueKind.Null) {
			if (config.ValueKind != JsonValueKind.Array) {
				throw new AcpProtocolException("ACP configOptions must be an array when present.");
			}
			ReadConfigOptionsLocked(config);
		}
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
		if (!update.TryGetProperty("configOptions", out var config) || config.ValueKind != JsonValueKind.Array) {
			throw new AcpProtocolException("An ACP config update is missing configOptions.");
		}
		lock (_gate) {
			ReplaceConfigOptionsLocked(config);
		}
		RaiseControls();
	}

	private void ReplaceConfigOptionsLocked(JsonElement config) {
		var legacyMode = _configOwnsMode ? null : _controls.GetValueOrDefault("mode");
		_controls.Clear();
		ReadConfigOptionsLocked(config);
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
				InsertText = "/" + name + " ",
			};
		}).ToArray();
		if (parsed.Select(command => command.Name).Distinct(StringComparer.Ordinal).Count() != parsed.Length) {
			throw new AcpProtocolException("ACP available commands repeat a name.");
		}
		lock (_gate) {
			_commands = parsed;
		}
		RaiseControls();
	}

	private void ReadConfigOptionsLocked(JsonElement config) {
		foreach (var option in config.EnumerateArray()) {
			string id = RequiredString(option, "id", "session config option");
			if (_controls.ContainsKey(id)) {
				throw new AcpProtocolException($"ACP repeated session config option '{id}'.");
			}
			string kind = RequiredString(option, "type", "session config option");
			string value;
			IReadOnlyList<AgentControlOption> values;
			if (kind == "boolean") {
				if (!option.TryGetProperty("currentValue", out var current)
					|| current.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) {
					throw new AcpProtocolException($"Boolean ACP config option '{id}' has no boolean currentValue.");
				}
				value = current.GetBoolean().ToString().ToLowerInvariant();
				values = [
					new AgentControlOption { Id = "true", Label = "On" },
					new AgentControlOption { Id = "false", Label = "Off" },
				];
			} else if (kind == "select") {
				value = RequiredString(option, "currentValue", "session config option");
				if (!option.TryGetProperty("options", out var choices) || choices.ValueKind != JsonValueKind.Array) {
					throw new AcpProtocolException($"Select ACP config option '{id}' has no options.");
				}
				values = ReadSelectOptions(id, choices);
				if (values.All(choice => choice.Id != value)) {
					throw new AcpProtocolException(
						$"Select ACP config option '{id}' has unadvertised currentValue '{value}'.");
				}
			} else {
				throw new AcpProtocolException($"Unsupported ACP config option type '{kind}'.");
			}
			_controls[id] = new AgentControlAxis {
				Id = id,
				Label = RequiredString(option, "name", "session config option"),
				Description = OptionalString(option, "description"),
				Category = OptionalString(option, "category"),
				Kind = kind,
				Value = value,
				ValueLabel = values.FirstOrDefault(choice => choice.Id == value)?.Label ?? value,
				Options = values,
			};
		}
	}

	private static IReadOnlyList<AgentControlOption> ReadSelectOptions(string configId, JsonElement choices) {
		var entries = choices.EnumerateArray().ToArray();
		bool grouped = entries.Length > 0 && entries.All(choice => choice.TryGetProperty("options", out _));
		if (!grouped && entries.Any(choice => choice.TryGetProperty("options", out _))) {
			throw new AcpProtocolException($"Select ACP config option '{configId}' mixes grouped and flat values.");
		}
		var values = grouped
			? entries.SelectMany(group => {
				RequiredString(group, "group", "session config group");
				string name = RequiredString(group, "name", "session config group");
				if (!group.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array) {
					throw new AcpProtocolException($"Select ACP config group '{name}' has no options.");
				}
				return options.EnumerateArray().Select(choice => SelectOption(choice, name));
			})
			: entries.Select(choice => SelectOption(choice, null));
		var result = values.ToArray();
		if (result.Select(option => option.Id).Distinct(StringComparer.Ordinal).Count() != result.Length) {
			throw new AcpProtocolException($"Select ACP config option '{configId}' repeats a value id.");
		}
		return result;
	}

	private static AgentControlOption SelectOption(JsonElement choice, string? group) => new() {
		Id = RequiredString(choice, "value", "session config value"),
		Label = RequiredString(choice, "name", "session config value"),
		Description = OptionalString(choice, "description"),
		Group = group,
	};

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
