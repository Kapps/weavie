using Weavie.Core.Agents;

namespace Weavie.Core.Tests;

/// <summary>
/// A consultable provider whose probes the test completes by hand. Completing on a thread without xUnit's
/// synchronization context runs the catalog's continuation inline, so its effect is visible once the call returns.
/// </summary>
internal sealed class FakeConsultProvider(string id) : IAgentConsultProvider {
	private readonly Lock _gate = new();
	private readonly List<(TaskCompletionSource<IReadOnlyList<AgentControlAxis>> Result, CancellationToken Token)> _probes = [];

	public AgentProviderInfo Info { get; } = new() {
		Id = id,
		Name = $"Fake {id}",
		Capabilities = AgentProviderCapabilities.StructuredPane,
		Available = true,
	};

	public Func<AgentConsultRequest, CancellationToken, Task<AgentConsultOutcome>> Consult { get; set; } =
		(request, _) => Task.FromResult<AgentConsultOutcome>(new AgentConsultSuccess {
			ModelId = request.Model,
			Controls = [],
			Reply = "ok",
			Denied = [],
		});

	public IReadOnlyList<(TaskCompletionSource<IReadOnlyList<AgentControlAxis>> Result, CancellationToken Token)> Probes {
		get { lock (_gate) return [.. _probes]; }
	}

	public static AgentControlAxis Models(params string[] ids) => new() {
		Id = "model",
		Label = "Model",
		Category = "model",
		Kind = "select",
		Value = ids[0],
		ValueLabel = ids[0],
		Options = [.. ids.Select(model => new AgentControlOption { Id = model, Label = model.ToUpperInvariant() })],
	};

	public Task CompleteProbe(int index, params AgentControlAxis[] controls) =>
		Task.Run(() => Probes[index].Result.SetResult(controls));

	public Task FailProbe(int index, string message) =>
		Task.Run(() => Probes[index].Result.SetException(new InvalidOperationException(message)));

	public Task<IReadOnlyList<AgentControlAxis>> ProbeControlsAsync(CancellationToken ct) {
		var result = new TaskCompletionSource<IReadOnlyList<AgentControlAxis>>();
		lock (_gate) _probes.Add((result, ct));
		return result.Task;
	}

	public Task<AgentConsultOutcome> ConsultAsync(AgentConsultRequest request, CancellationToken ct) => Consult(request, ct);

	public void ClearConversation(string workspace) => throw new NotSupportedException();

	public IAgentSession CreateSession(AgentSessionContext context) => throw new NotSupportedException();
}
