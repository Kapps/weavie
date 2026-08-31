using System.IO.Pipes;
using Weavie.Core.Hooks;

namespace Weavie.Hosting.Desktop;

/// <summary>Hands the paths a launch was given to an already-running instance, when there is one.</summary>
public static class InstanceClient {
	/// <summary>
	/// Offers <paramref name="paths"/> to the instance serving <paramref name="weavieRoot"/>. Returns the
	/// running instance's answer, or "not handled" when none is listening — which is the ordinary first launch,
	/// so it must be fast rather than thorough.
	/// </summary>
	public static async Task<HandoffReply> OfferAsync(
		string weavieRoot,
		IReadOnlyList<string> paths,
		CancellationToken ct) {
		ArgumentException.ThrowIfNullOrEmpty(weavieRoot);
		ArgumentNullException.ThrowIfNull(paths);

		try {
			using var client = new NamedPipeClientStream(
				".", InstanceProtocol.PipeName(weavieRoot), PipeDirection.InOut,
				PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
			await client.ConnectAsync(InstanceProtocol.ConnectTimeoutMs, ct).ConfigureAwait(false);
			await HookProtocol
				.WriteFramedAsync(client, InstanceProtocol.EncodeRequest(paths), ct)
				.ConfigureAwait(false);
			return await HookProtocol.ReadFramedAsync(client, ct).ConfigureAwait(false) is { } reply
				? InstanceProtocol.DecodeReply(reply)
				: new HandoffReply(false, string.Empty);
		} catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException
			or UnauthorizedAccessException) {
			return new HandoffReply(false, string.Empty);
		}
	}
}
