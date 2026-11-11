using Weavie.Core.Agents;
using Weavie.Core.Sessions;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

// One session's view of the sessions on this host, /btw side sessions included. See docs/specs/session-messaging.md.
public sealed partial class HostCore {
	private Task<IReadOnlyList<SessionPeer>> ListSessionPeersAsync(bool includeSides, CancellationToken ct) =>
		_ui.InvokeAsync(() => Task.FromResult<IReadOnlyList<SessionPeer>>([.. BuildSessionCatalog().SelectMany(entry => {
			var peer = new SessionPeer(
				entry.Id,
				entry.Title,
				entry.ProviderId,
				entry.Loaded,
				entry.Status,
				entry.AgentSurface != "structured" ? SessionPeerReceive.Unsupported
					: entry.Loaded ? SessionPeerReceive.Ready
					: SessionPeerReceive.NeedsLoad,
				Parent: null,
				Prompt: null);
			return includeSides && _sessions?.Find(entry.Id)?.Session?.Agent.SideConversations is { } sides
				? sides.Asides.Select(side => peer with {
					Id = SideId(entry.Id, side.Id),
					Title = SideTitle(entry.Title),
					Status = StatusName(side.Running ? SessionStatus.Working : SessionStatus.Idle),
					Parent = entry.Id,
					Prompt = side.Prompt,
				}).Prepend(peer)
				: [peer];
		})]), ct);

	private async Task<SessionPeerTranscript> ReadSessionPeerAsync(string sessionId, int count, CancellationToken ct) {
		var (slot, side) = RequirePeer(sessionId);
		string title = SessionName.Title(slot.Label);
		IReadOnlyList<AgentTranscriptEntry> entries;
		if (side is not null) {
			entries = await _ui.InvokeAsync(() => Task.FromResult(
				RequireLoaded(slot).Agent is { SideConversations: { } sides } agent && sides.Asides.Any(aside => aside.Id == side)
					? AgentTranscript.FromPaneJournal(agent.Output.View(slot.AgentProviderId, side).Rows)
					: throw UnknownPeer(sessionId)), ct).ConfigureAwait(false);
			return new(sessionId, SideTitle(title), entries.Count, [.. entries.TakeLast(count)]);
		}
		try {
			entries = await Task.Run(
				() => _agentProviders.RequireAvailable(slot.AgentProviderId).ReadTranscript(slot.WorktreePath),
				ct).ConfigureAwait(false);
		} catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException) {
			throw new SessionPeerException($"Couldn't read session '{slot.Id}': {ex.Message}");
		}
		return new(slot.Id, title, entries.Count, [.. entries.TakeLast(count)]);
	}

	private async Task<SessionPeerDelivery> SendSessionPeerAsync(
		SessionAddress source,
		string? sourceSide,
		string sessionId,
		string message,
		CancellationToken ct) {
		var (slot, side) = RequirePeer(sessionId);
		bool sameSession = slot.Id == source.Slot;
		if (sameSession && side == sourceSide) throw new SessionPeerException("A session can't message itself.");
		var sender = sameSession ? slot : await CurrentSourceAsync(source, ct).ConfigureAwait(false) ?? throw SenderGone();
		return await GatedAsync(slot.Lifecycle, () => _ui.InvokeAsync(() => {
			if (sameSession && slot.Session?.Address != source) throw SenderGone();
			if (!ReferenceEquals(_sessions?.Find(slot.Id), slot)) throw UnknownPeer(sessionId);
			var target = RequireLoaded(slot);
			if (target.Agent.Structured is not { } structured) {
				throw new SessionPeerException(
					$"Session '{slot.Id}' runs {target.Agent.Provider.Name} in a terminal, which can't take messages from other sessions.");
			}
			if (_drainInputFrozen) throw new SessionPeerException("Agent input is paused while Weavie restarts.");
			string title = SessionName.Title(sender.Label);
			var submission = AgentTurnSubmission.Prompt(message) with {
				Sender = sourceSide is null ? new(sender.Id, title) : new(SideId(sender.Id, sourceSide), SideTitle(title)),
			};
			if (side is null) {
				structured.Submit(submission);
				return Task.FromResult(new SessionPeerDelivery(slot.Id, StatusName(target.Status.Status)));
			}
			try {
				(target.Agent.SideConversations ?? throw UnknownPeer(sessionId)).ReplyAside(side, submission);
			} catch (InvalidOperationException ex) {
				throw new SessionPeerException($"Session '{sessionId}' can't take the message: {ex.Message}");
			}
			return Task.FromResult(new SessionPeerDelivery(sessionId, StatusName(SessionStatus.Working)));
		}, ct), ct).ConfigureAwait(false);
	}

	// A /btw side session's id is its session's id, '#', and its conversation id; a session's own id wins.
	private (SessionSlot Slot, string? Side) RequirePeer(string id) {
		if (_sessions?.Find(id) is { } slot) return (slot, null);
		int at = id.LastIndexOf('#');
		return at > 0 && _sessions?.Find(id[..at]) is { } parent ? (parent, id[(at + 1)..]) : throw UnknownPeer(id);
	}

	private static string SideId(string sessionId, string conversationId) => $"{sessionId}#{conversationId}";

	private static string SideTitle(string sessionTitle) => $"{sessionTitle} BTW";

	private static HostSession RequireLoaded(SessionSlot slot) => slot.Session
		?? throw new SessionPeerException($"Session '{slot.Id}' isn't loaded. Load it with weavie.session.load (id '{slot.Id}') first.");

	private static SessionPeerException SenderGone() => new("The sending session no longer exists.");

	private static SessionPeerException UnknownPeer(string sessionId) =>
		new($"No session has id '{sessionId}'. Call listSessions for the exact ids.");

	/// <summary>The peer view owned by the session at <paramref name="self"/>, or by its /btw side session <paramref name="side"/>.</summary>
	private sealed class BoundSessionPeers(HostCore core, SessionAddress self, string? side) : ISessionPeers {
		public string SelfId => side is null ? self.Slot : SideId(self.Slot, side);

		public string? ParentId => side is null ? null : self.Slot;

		public ISessionPeers Side(string conversationId) => new BoundSessionPeers(core, self, conversationId);

		public Task<IReadOnlyList<SessionPeer>> ListAsync(bool includeSides, CancellationToken ct) => core.ListSessionPeersAsync(includeSides, ct);

		public Task<SessionPeerTranscript> ReadAsync(string sessionId, int count, CancellationToken ct) =>
			core.ReadSessionPeerAsync(sessionId, count, ct);

		public Task<SessionPeerDelivery> SendAsync(string sessionId, string message, CancellationToken ct) =>
			core.SendSessionPeerAsync(self, side, sessionId, message, ct);
	}
}
