using Weavie.Core.Agents;
using Weavie.Core.Configuration;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private readonly AgentImageFetcher _agentImages = new(new SocketsHttpHandler());

	/// <summary>Fetches a remote image an agent's reply in session <paramref name="address"/> shows; null when that session is gone.</summary>
	internal async Task<AgentImage?> FetchAgentImageAsync(SessionAddress address, string url, CancellationToken ct) {
		var session = _sessions?.Find(address.Slot)?.Session;
		if (session?.Address != address) {
			return null;
		}

		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Background.Stopping);
		return await _agentImages.FetchAsync(url, _settings.RequireBool(AgentSettings.RemoteImages), cancellation.Token)
			.ConfigureAwait(false);
	}
}
