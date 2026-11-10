using System.Text.Json;
using System.Text.Json.Nodes;
using Weavie.Core.Agents;
using static Weavie.AgentClientProtocol.AcpJson;

namespace Weavie.AgentClientProtocol;

internal sealed partial class AcpConversation {
	private async Task RestoreControlDefaultsAsync() {
		var held = _port.HeldControls();
		var pending = new Dictionary<string, string>(_port.ControlDefaults(), StringComparer.Ordinal);
		foreach (var (axis, value) in held) pending[axis] = value;
		while (pending.Count > 0) {
			AcpControlMutation? mutation = null;
			AcpControlMutation? stale = null;
			bool consumed = false;
			lock (_gate) {
				// The model goes first, because the options an agent advertises depend on it.
				foreach (var control in _controls.Values.OrderBy(control => control.Category != "model")) {
					if (!pending.Remove(control.Id, out string? value)) continue;
					consumed = true;
					if (control.Options.All(option => option.Id != value)) stale = new AcpControlMutation(control.Id, value);
					else if (control.Value != value) mutation = new AcpControlMutation(control.Id, value);
					break;
				}
			}
			if (stale is not null && held.ContainsKey(stale.Axis)) {
				EmitFailure(new AcpProtocolException(
					$"{Definition.Name} no longer advertises '{stale.Value}' for the '{stale.Axis}' control the workflow stage holds."));
				continue;
			}
			if (stale is not null) {
				_port.ForgetControl(stale.Axis, stale.Value);
				EmitFailure(new AcpProtocolException(
					$"Saved {Definition.Name} control '{stale.Axis}' value '{stale.Value}' is no longer advertised and was forgotten."));
				continue;
			}
			if (mutation is null) {
				if (consumed) continue;
				break;
			}
			await DeliverControlMutationAsync(mutation, persist: false).ConfigureAwait(false);
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
		ApplyReportedControls(() => ReplaceConfigOptionsLocked(config));
	}

	// A mode the agent switches itself, such as on an approved plan, is the user's latest choice to restore.
	private void ApplyReportedControls(Action updateLocked) {
		string? mode;
		lock (_gate) {
			string? before = _controls.GetValueOrDefault("mode")?.Value;
			updateLocked();
			mode = _controls.GetValueOrDefault("mode")?.Value;
			if (_loadingTranscript || mode == before) mode = null;
		}
		if (mode is not null) _port.RememberControl("mode", mode);
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
		ApplyReportedControls(() => {
			if (_controls.TryGetValue("mode", out var mode)) {
				if (mode.Options.All(option => option.Id != value)) {
					throw new AcpProtocolException($"ACP current mode update selected unadvertised mode '{value}'.");
				}
				_controls["mode"] = WithValue(mode, value);
			}
		});
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

	private void RaiseControls() => _port.ControlsChanged();
}
