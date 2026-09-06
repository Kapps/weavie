using Weavie.Hosting.Agents;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	internal async Task<bool> WriteAgentHistoryAsync(
		SessionAddress address,
		AgentPaneHistoryRequest request,
		Stream output,
		CancellationToken ct) {
		var session = _sessions?.Find(address.Slot)?.Session;
		if (session?.Address != address) {
			return false;
		}

		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Background.Stopping);
		cancellation.Token.ThrowIfCancellationRequested();
		var snapshot = session.Agent.ReadHistory(request);
		await AgentPaneProtocol.WriteHistoryAsync(snapshot, output, cancellation.Token).ConfigureAwait(false);
		return true;
	}
}
