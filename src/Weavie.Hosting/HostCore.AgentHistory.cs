using Weavie.Hosting.Agents;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	internal async Task<bool> WriteAgentHistoryAsync(
		SessionAddress address,
		AgentPaneHistoryRequest request,
		Stream output,
		CancellationToken ct) {
		if (ExactSession(address) is not { } session) {
			return false;
		}

		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Background.Stopping);
		cancellation.Token.ThrowIfCancellationRequested();
		var snapshot = session.Agent.ReadHistory(request);
		await AgentPaneProtocol.WriteHistoryAsync(snapshot, output, cancellation.Token).ConfigureAwait(false);
		return true;
	}

	// A session resource names one incarnation, so a reloaded session's old address finds nothing.
	private HostSession? ExactSession(SessionAddress address) =>
		_sessions?.Find(address.Slot)?.Session is { } session && session.Address == address ? session : null;
}
