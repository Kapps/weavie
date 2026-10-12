using Weavie.Core.Processes;

namespace Weavie.AgentClientProtocol;

/// <summary>Ends an ACP agent the way the protocol does: stdin EOF, force-killed past the deadline.</summary>
internal static class AcpProcessStop {
	private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

	/// <summary>
	/// Closes the agent's input once the write in flight under <paramref name="writeGate"/> finishes, waits for the
	/// exit, and releases the process's streams only after any write cut off by a kill has settled.
	/// </summary>
	public static async Task<ProcessStopOutcome> StopAsync(OwnedProcess process, SemaphoreSlim writeGate, ISupervisorClock clock) {
		try {
			return await process.StopAsync(ProcessStopPlan.CloseInput(process, writeGate, Deadline), clock).ConfigureAwait(false);
		} finally {
			await writeGate.WaitAsync().ConfigureAwait(false);
			try {
				process.Dispose();
			} finally {
				writeGate.Release();
			}
		}
	}
}
