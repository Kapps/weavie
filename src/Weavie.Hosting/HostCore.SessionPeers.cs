using Weavie.Core.Agents;
using Weavie.Core.Sessions;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

// One session's view of the other sessions on this host. See docs/specs/session-messaging.md.
public sealed partial class HostCore {
	private Task<IReadOnlyList<SessionPeer>> ListSessionPeersAsync(CancellationToken ct) =>
		_ui.InvokeAsync(() => Task.FromResult<IReadOnlyList<SessionPeer>>([.. BuildSessionCatalog().Select(entry =>
			new SessionPeer(
				entry.Id,
				entry.Title,
				entry.ProviderId,
				entry.Loaded,
				entry.Status,
				entry.AgentSurface != "structured" ? SessionPeerReceive.Unsupported
					: entry.Loaded ? SessionPeerReceive.Ready
					: SessionPeerReceive.NeedsLoad))]), ct);

	private async Task<SessionPeerTranscript> ReadSessionPeerAsync(string sessionId, int count, CancellationToken ct) {
		var slot = RequirePeer(sessionId);
		IReadOnlyList<AgentTranscriptEntry> entries;
		try {
			entries = await Task.Run(
				() => _agentProviders.RequireAvailable(slot.AgentProviderId).ReadTranscript(slot.WorktreePath),
				ct).ConfigureAwait(false);
		} catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException) {
			throw new SessionPeerException($"Couldn't read session '{slot.Id}': {ex.Message}");
		}
		return new(slot.Id, SessionName.Title(slot.Label), entries.Count, [.. entries.TakeLast(count)]);
	}

	private async Task<SessionPeerDelivery> SendSessionPeerAsync(
		SessionAddress source,
		string sessionId,
		string message,
		CancellationToken ct) {
		if (sessionId == source.Slot) throw new SessionPeerException("A session can't message itself.");
		var sender = await CurrentSourceAsync(source, ct).ConfigureAwait(false)
			?? throw new SessionPeerException("The sending session no longer exists.");
		var slot = RequirePeer(sessionId);
		return await GatedAsync(slot.Lifecycle, () => _ui.InvokeAsync(() => {
			if (!ReferenceEquals(_sessions?.Find(slot.Id), slot)) throw UnknownPeer(sessionId);
			if (slot.Session is not { } target) {
				throw new SessionPeerException(
					$"Session '{slot.Id}' isn't loaded. Load it with weavie.session.load (id '{slot.Id}') first.");
			}
			if (target.Agent.Structured is not { } structured) {
				throw new SessionPeerException(
					$"Session '{slot.Id}' runs {target.Agent.Provider.Name} in a terminal, which can't take messages from other sessions.");
			}
			if (_drainInputFrozen) throw new SessionPeerException("Agent input is paused while Weavie restarts.");
			structured.Submit(AgentTurnSubmission.Prompt(message) with {
				Sender = new(sender.Id, SessionName.Title(sender.Label)),
			});
			return Task.FromResult(new SessionPeerDelivery(slot.Id, StatusName(target.Status.Status)));
		}, ct), ct).ConfigureAwait(false);
	}

	private SessionSlot RequirePeer(string sessionId) => _sessions?.Find(sessionId) ?? throw UnknownPeer(sessionId);

	private static SessionPeerException UnknownPeer(string sessionId) =>
		new($"No session has id '{sessionId}'. Call listSessions for the exact ids.");

	/// <summary>The peer view owned by the session at <paramref name="self"/>.</summary>
	private sealed class BoundSessionPeers(HostCore core, SessionAddress self) : ISessionPeers {
		public string SelfId => self.Slot;

		public Task<IReadOnlyList<SessionPeer>> ListAsync(CancellationToken ct) => core.ListSessionPeersAsync(ct);

		public Task<SessionPeerTranscript> ReadAsync(string sessionId, int count, CancellationToken ct) =>
			core.ReadSessionPeerAsync(sessionId, count, ct);

		public Task<SessionPeerDelivery> SendAsync(string sessionId, string message, CancellationToken ct) =>
			core.SendSessionPeerAsync(self, sessionId, message, ct);
	}
}
