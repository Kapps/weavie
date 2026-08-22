using Weavie.Core.Changes;
using Weavie.Core.Editor;
using Weavie.Core.Inference;
using Weavie.Core.Revise;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	/// <summary>
	/// Starts revising one region the user selected, under their own instruction. The region's text travels with
	/// the request and guards the write, so an edit landing while the query runs aborts it rather than clobbering
	/// it. Every failure — here or downstream — reaches the user as a toast on this session's bus.
	/// </summary>
	private void StartRevise(HostSession session, ReviseStartMessage message) {
		string instruction = message.Instruction.Trim();
		if (instruction.Length == 0) {
			Notify(session, "warn", "Type what you want done to the selection.");
			return;
		}

		// Every path-taking handler guards first: an unguarded path here would write anywhere on disk.
		if (!BufferStore.IsWithinWorkspace(session.WorkspaceRoot, message.Path)) {
			Notify(session, "warn", "That file is outside this session's workspace.");
			return;
		}

		if (SlotFor(session) is not { } slot) {
			Notify(session, "warn", "That session is no longer loaded.");
			return;
		}

		// A revision runs as long as the provider takes, so it never occupies the bus request lane.
		_ = session.Background.Run(ct => session.Revise.RunAsync(
			new InferenceOwner { AgentProviderId = slot.AgentProviderId, Workspace = session.WorkspaceRoot },
			[
				new ReviseTarget {
					Path = message.Path,
					Range = new LineRange(message.StartLine, message.EndLineExclusive),
					OriginalText = message.OriginalText,
				},
			],
			instruction,
			InferenceInvocationOrigin.UserInitiated,
			ct));
	}

	private sealed record ReviseStartMessage(
		string Path,
		int StartLine,
		int EndLineExclusive,
		string OriginalText,
		string Instruction);
}
