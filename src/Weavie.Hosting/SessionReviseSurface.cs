using Weavie.Core.Revise;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

/// <summary>
/// One session's revise surface over its own bus: the in-flight set goes to every client, the commit probe goes to
/// the bound view (the only party that knows whether its buffer is dirty), and failures become toasts.
/// </summary>
internal sealed class SessionReviseSurface : IReviseSurface {
	private readonly HostSession _session;

	public SessionReviseSurface(HostSession session) {
		ArgumentNullException.ThrowIfNull(session);
		_session = session;
	}

	/// <inheritdoc/>
	public void Publish(IReadOnlyList<ReviseRegion> inFlight) =>
		_session.Bus.Feature("revise").Publish("state", WireJson.Default.ReviseState, new([
			.. inFlight.Select(region => new ReviseRegionWire(
				region.Id,
				region.Path,
				region.Range.Start,
				region.Range.EndExclusive,
				region.OriginalText)),
		]));

	/// <inheritdoc/>
	public async Task<string?> ConfirmAsync(ReviseRegion region, CancellationToken cancellationToken) {
		// No attached page means no editor holds the file, so there is nothing to object to the write.
		var reply = await _session.View.Feature("revise")
			.TryRequestAsync(
				"confirm", WireJson.Default.ReviseConfirmRequest, WireJson.Default.ReviseConfirmReply,
				new ReviseConfirmRequest(region.Id),
				cancellationToken)
			.ConfigureAwait(false);
		return reply is null || reply.Ok ? null : reply.Reason;
	}

	/// <inheritdoc/>
	public void Failed(ReviseRegion region, string reason) =>
		_session.Notify("warn", $"Couldn't revise {Path.GetFileName(region.Path)}: {reason}.");
}

/// <summary>Asks the bound view whether a region's write may land.</summary>
/// <param name="Id">The region the write would replace.</param>
internal sealed record ReviseConfirmRequest(int Id);

/// <summary>The view's answer to a commit probe.</summary>
/// <param name="Ok">Whether the write may land.</param>
/// <param name="Reason">Why it must not, when <paramref name="Ok"/> is false.</param>
internal sealed record ReviseConfirmReply(bool Ok, string Reason);

internal sealed record ReviseState(IReadOnlyList<ReviseRegionWire> Regions);

internal sealed record ReviseRegionWire(int Id, string Path, int StartLine, int EndLineExclusive, string OriginalText);
