using System.Text.Json;
using System.Text.Json.Nodes;
using Weavie.Core.Agents;

namespace Weavie.AgentClientProtocol;

// Control changes reach the agent one request at a time, in the order they were asked for: the picker's, which are
// remembered as the user's defaults, and awaited batches, which are not and complete once the agent confirms them.
internal sealed partial class AcpConversation {
	internal void SetControl(string axis, string value) {
		ArgumentException.ThrowIfNullOrEmpty(axis);
		ArgumentException.ThrowIfNullOrEmpty(value);
		string? unadvertised;
		lock (_gate) {
			unadvertised = UnadvertisedLocked(new AcpControlMutation(axis, value));
			if (unadvertised is null) Enqueue(new AcpControlRequest([new AcpControlMutation(axis, value)], null));
		}
		if (unadvertised is not null) EmitFailure(new AcpProtocolException(unadvertised));
		else DispatchControlMutation();
	}

	internal Task ApplyControlsAsync(IReadOnlyList<AgentControlValue> values) {
		ArgumentNullException.ThrowIfNull(values);
		var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		lock (_gate) Enqueue(new AcpControlRequest([.. values.Select(v => new AcpControlMutation(v.Axis, v.Value))], applied));
		var replaced = _lifetime.Token.Register(() => applied.TrySetException(Replaced()));
		_ = applied.Task.ContinueWith(_ => replaced.Dispose(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
		DispatchControlMutation();
		return applied.Task;
	}

	private void Enqueue(AcpControlRequest request) {
		if (!_ready || _sessionId is null) throw new InvalidOperationException("The ACP session is not ready.");
		_controlMutations.Enqueue(request);
	}

	private void DispatchControlMutation() {
		AcpControlRequest request;
		lock (_gate) {
			if (_controlMutationActive || _controlMutations.Count == 0 || !_ready || !Live) {
				return;
			}
			_controlMutationActive = true;
			request = _controlMutations.Dequeue();
		}
		_ = Task.Run(async () => {
			try {
				await DeliverAsync(request).ConfigureAwait(false);
				if (Live) request.Applied?.TrySetResult();
				else request.Applied?.TrySetException(Replaced());
			} catch (Exception ex) when (ex is not OperationCanceledException) {
				request.Applied?.TrySetException(ex is AcpRequestException ? new AgentControlException(ex.Message) : ex);
				lock (_turnTransitionGate) {
					if (Live && !(request.Applied is not null && ex is AgentControlException or AcpRequestException)) {
						if (ex is IOException or AcpProtocolException) FailRuntimeSerialized(ex);
						else EmitFailure(ex);
					}
				}
			} catch (OperationCanceledException) {
				request.Applied?.TrySetException(Replaced());
			} finally {
				lock (_gate) {
					if (Live) _controlMutationActive = false;
				}
				DispatchControlMutation();
			}
		});
	}

	// An awaited batch is checked against what the agent advertises as each change is reached, since a model switch
	// changes the options; a value already in effect needs no request.
	private async Task DeliverAsync(AcpControlRequest request) {
		foreach (var mutation in request.Mutations) {
			if (request.Applied is not null) {
				string? unadvertised;
				bool current;
				lock (_gate) {
					unadvertised = UnadvertisedLocked(mutation);
					current = unadvertised is null && _controls[mutation.Axis].Value == mutation.Value;
				}
				if (unadvertised is not null) throw new AgentControlException(unadvertised);
				if (current) continue;
			}
			await DeliverControlMutationAsync(mutation, persist: request.Applied is null).ConfigureAwait(false);
		}
	}

	private string? UnadvertisedLocked(AcpControlMutation mutation) =>
		!_controls.TryGetValue(mutation.Axis, out var control)
			? $"{Definition.Name} doesn't advertise the '{mutation.Axis}' control; it advertises {string.Join(", ", _controls.Keys)}."
			: control.Options.All(option => option.Id != mutation.Value)
				? $"{Definition.Name} doesn't advertise '{mutation.Value}' for the '{mutation.Axis}' control; it advertises "
					+ $"{string.Join(", ", control.Options.Select(option => option.Id))}."
				: null;

	private static AgentControlException Replaced() =>
		new("The conversation was replaced before the agent confirmed the control change.");

	private async Task DeliverControlMutationAsync(AcpControlMutation mutation, bool persist) {
		AgentControlAxis control;
		bool mode;
		lock (_gate) {
			if (_sessionId is null) return;
			if (!_controls.TryGetValue(mutation.Axis, out control!)
				|| control.Options.All(option => option.Id != mutation.Value)) {
				throw new AcpProtocolException(
					$"ACP no longer advertises '{mutation.Value}' for the '{mutation.Axis}' control.");
			}
			mode = mutation.Axis == "mode" && !_configOwnsMode;
		}

		JsonElement result;
		if (mode) {
			result = await _endpoint.Value.RequestAsync(
				"session/set_mode",
				new JsonObject { ["modeId"] = mutation.Value },
				CancellationToken.None).ConfigureAwait(false);
		} else {
			result = await _endpoint.Value.RequestAsync(
				"session/set_config_option",
				AcpConfigurationOptions.SetParameters(control, mutation.Value),
				CancellationToken.None).ConfigureAwait(false);
		}
		lock (_turnTransitionGate) {
			string? remembered;
			if (!Live) return;
			lock (_gate) {
				if (mode) _controls[mutation.Axis] = WithValue(control, mutation.Value);
				else ReadControlResultLocked(result);
				remembered = persist ? _controls[mutation.Axis].Value : null;
			}
			if (remembered is not null) _port.RememberControl(mutation.Axis, remembered);
			RaiseControls();
		}
	}

	private sealed record AcpControlRequest(IReadOnlyList<AcpControlMutation> Mutations, TaskCompletionSource? Applied);
}
