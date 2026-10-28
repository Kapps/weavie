using Weavie.Core.Agents;
using Weavie.Core.Mcp;

namespace Weavie.Core.Sessions;

/// <summary>
/// The other sessions on this host, as seen from one session. Bound to its caller at construction, so every call
/// comes from that session by definition. See docs/specs/session-messaging.md.
/// </summary>
public interface ISessionPeers {
	/// <summary>The id of the session this view belongs to.</summary>
	string SelfId { get; }

	/// <summary>Every session on this host, including the caller's own.</summary>
	Task<IReadOnlyList<SessionPeer>> ListAsync(CancellationToken ct);

	/// <summary>The last <paramref name="count"/> entries of <paramref name="sessionId"/>'s primary conversation.</summary>
	/// <exception cref="SessionPeerException">The session is unknown or its conversation can't be read.</exception>
	Task<SessionPeerTranscript> ReadAsync(string sessionId, int count, CancellationToken ct);

	/// <summary>Submits <paramref name="message"/> to <paramref name="sessionId"/>'s agent, attributed to the caller.</summary>
	/// <exception cref="SessionPeerException">The session can't receive messages; the message says why.</exception>
	Task<SessionPeerDelivery> SendAsync(string sessionId, string message, CancellationToken ct);
}

/// <summary>Whether a session can be sent a message now.</summary>
public enum SessionPeerReceive {
	/// <summary>Its agent takes messages now.</summary>
	Ready,

	/// <summary>It must be loaded first.</summary>
	NeedsLoad,

	/// <summary>Its agent can't take messages from other sessions.</summary>
	Unsupported,
}

/// <summary>One session as listed to a peer.</summary>
public sealed record SessionPeer(
	string Id,
	string Title,
	string ProviderId,
	bool Loaded,
	string Status,
	SessionPeerReceive Receive);

/// <summary>The tail of a session's primary conversation.</summary>
public sealed record SessionPeerTranscript(string Id, string Title, int Total, IReadOnlyList<AgentTranscriptEntry> Entries);

/// <summary>A message accepted into a session's input queue, with that session's status at delivery.</summary>
public sealed record SessionPeerDelivery(string Id, string Status);

/// <summary>A peer request Weavie refused or could not complete; the message names the cause.</summary>
public sealed class SessionPeerException(string message) : Exception(message);

/// <summary>The peer view for servers that do not belong to a session.</summary>
public sealed class NoSessionPeers : ISessionPeers {
	/// <summary>The shared instance.</summary>
	public static readonly NoSessionPeers Instance = new();

	private NoSessionPeers() { }

	/// <inheritdoc/>
	public string SelfId => throw Unbound();

	/// <inheritdoc/>
	public Task<IReadOnlyList<SessionPeer>> ListAsync(CancellationToken ct) => throw Unbound();

	/// <inheritdoc/>
	public Task<SessionPeerTranscript> ReadAsync(string sessionId, int count, CancellationToken ct) => throw Unbound();

	/// <inheritdoc/>
	public Task<SessionPeerDelivery> SendAsync(string sessionId, string message, CancellationToken ct) => throw Unbound();

	private static ToolUnavailableException Unbound() => new("This server does not belong to a session.");
}
