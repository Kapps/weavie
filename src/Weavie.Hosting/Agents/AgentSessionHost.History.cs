namespace Weavie.Hosting.Agents;

public sealed partial class AgentSessionHost {
	internal void StartStructured() {
		if (Structured is null) return;
		PublishPlans();
		Structured.Start();
	}

	internal Weavie.Core.Agents.AgentPaneHistory ReadHistory(AgentPaneHistoryRequest request) =>
		Output.ReadHistory(request.KnownGeneration, request.KnownRevision);

	internal Weavie.Core.Agents.AgentPaneRecord ReadRecord(AgentPaneRecordRequest request) =>
		Output.ReadRecord(request.Generation, request.Ordinal);
}
