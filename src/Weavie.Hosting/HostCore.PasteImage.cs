using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Editor;
using Weavie.Core.Json;

namespace Weavie.Hosting;

// Remote image paste into an agent: the web captures bytes, the host writes a scratch file, and the active
// provider receives that file through its native image path. See docs/specs/remote-paste-image.md.
public sealed partial class HostCore {
	private void HandlePasteImage(JsonElement root) {
		// Frozen while an update restart commits: injecting now would seed a turn the restart discards (the same
		// guard term-input uses; see HostCore.Drain.cs).
		if (_drainInputFrozen) {
			return;
		}

		string mime = root.GetStringOrEmpty("mime");
		if (!PastedImageMedia.TryExtension(mime, out string extension)) {
			Notify("warn", $"Can't paste that image type ({(mime.Length == 0 ? "unknown" : mime)}) — use PNG, JPEG, GIF, or WebP.");
			return;
		}

		// Reject oversize by the base64 length BEFORE decoding, so a client bypassing the web pre-check (the
		// remote bridge is the trust boundary) can't force a large decode allocation past the cap. base64 is 4
		// chars per 3 bytes, so len/4*3 bounds the decoded size (accurate to the last group).
		string dataB64 = root.GetStringOrEmpty("dataB64");
		long approxBytes = (long)dataB64.Length / 4 * 3;
		if (approxBytes > PastedImageMedia.MaxBytes) {
			Notify(
				"warn",
				$"That image is {approxBytes / (1024.0 * 1024.0):0.0} MB — Weavie accepts agent images up to {PastedImageMedia.MaxBytes / (1024 * 1024)} MB. Resize it and paste again.");
			return;
		}

		// Bad base64 throws out to the OnWebMessage backstop (logged, not fatal), matching term-input.
		byte[] bytes = Convert.FromBase64String(dataB64);
		if (bytes.Length == 0) {
			return;
		}

		if (SessionForSlot(root) is not { } session) {
			return;
		}
		if (session.Claude is null) {
			Notify("warn", "This structured agent requires the current attachment protocol. Refresh Weavie and paste again.");
			return;
		}

		string path = session.PastedImages.Write(extension, bytes);
		session.SendAgentImagePath(path);
	}

	private void HandleAgentAttachmentUpload(JsonElement root) {
		string slot = root.GetStringOrEmpty("slot");
		string id = root.GetStringOrEmpty("id");
		try {
			if (_drainInputFrozen) {
				throw new InvalidOperationException("Agent input is paused while Weavie restarts.");
			}
			if (StructuredSession(slot) is not { } session) {
				throw new InvalidOperationException("That agent session is no longer loaded.");
			}

			string mime = root.GetStringOrEmpty("mime");
			if (!PastedImageMedia.TryExtension(mime, out string extension)) {
				throw new InvalidOperationException(
					$"Can't paste that image type ({(mime.Length == 0 ? "unknown" : mime)}) — use PNG, JPEG, GIF, or WebP.");
			}

			string dataB64 = root.GetStringOrEmpty("dataB64");
			long approxBytes = (long)dataB64.Length / 4 * 3;
			if (approxBytes > PastedImageMedia.MaxBytes) {
				throw new InvalidOperationException(
					$"That image is {approxBytes / (1024.0 * 1024.0):0.0} MB — Weavie accepts agent images up to {PastedImageMedia.MaxBytes / (1024 * 1024)} MB.");
			}

			byte[] bytes = Convert.FromBase64String(dataB64);
			if (bytes.Length == 0) {
				throw new InvalidOperationException("The pasted image was empty.");
			}

			var attachment = session.AgentAttachments.Add(id, mime, extension, bytes);
			PostAttachmentState(slot, id, attachment is null ? "removed" : "ready", string.Empty);
		} catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException) {
			PostAttachmentState(slot, id, "failed", ex.Message);
		}
	}

	private void HandleAgentAttachmentRemove(JsonElement root) {
		string slot = root.GetStringOrEmpty("slot");
		string id = root.GetStringOrEmpty("id");
		StructuredSession(slot)?.AgentAttachments.Remove(id);
		PostAttachmentState(slot, id, "removed", string.Empty);
	}

	private void HandleAgentSubmit(JsonElement root) {
		string slot = root.GetStringOrEmpty("slot");
		string id = root.GetStringOrEmpty("id");
		try {
			if (_drainInputFrozen) {
				throw new InvalidOperationException("Agent input is paused while Weavie restarts.");
			}
			if (StructuredSession(slot) is not { } session || session.Agent.Structured is not { } agent) {
				throw new InvalidOperationException("That agent session is no longer loaded.");
			}
			if (id.Length > 0 && session.AgentAttachments.TryReceipt(id, out var receipt)) {
				PostSubmissionState(slot, id, receipt, "accepted", string.Empty);
				return;
			}

			string text = root.GetStringOrEmpty("prompt");
			bool atomicSubmission = root.TryGetProperty("attachmentIds", out var attachments)
				&& attachments.ValueKind == JsonValueKind.Array;
			if (atomicSubmission && id.Length == 0) {
				throw new InvalidOperationException("The agent submission id is required.");
			}
			string[] attachmentIds = atomicSubmission
				? [.. attachments.EnumerateArray().Select(value => value.GetString() ?? string.Empty)]
				: [];
			string[] skills = root.TryGetProperty("skills", out var skillsElement) && skillsElement.ValueKind == JsonValueKind.Array
				? [.. skillsElement.EnumerateArray().Select(value => value.GetString() ?? string.Empty).Where(name => name.Length > 0)]
				: [];
			if (text.Trim().Length == 0 && attachmentIds.Length == 0 && skills.Length == 0) {
				throw new InvalidOperationException("Write a prompt, attach an image, or add a skill before running Codex.");
			}

			var resolved = session.AgentAttachments.Resolve(attachmentIds);
			agent.Submit(new AgentTurnSubmission { Id = id, Text = text, Attachments = resolved, Skills = skills });
			if (id.Length > 0) {
				session.AgentAttachments.CommitSubmission(id, attachmentIds);
			}
			PostSubmissionState(slot, id, attachmentIds, "accepted", string.Empty);
		} catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) {
			PostSubmissionState(slot, id, [], "rejected", ex.Message);
		}
	}

	private HostSession? StructuredSession(string slot) =>
		string.IsNullOrEmpty(slot) ? null : _sessions?.Find(slot)?.Session is { Agent.Structured: not null } session ? session : null;

	private void PostAttachmentState(string slot, string id, string status, string error) =>
		_bridge.PostToWeb(JsonSerializer.Serialize(new { type = "agent-attachment-state", slot, id, status, error }));

	private void PostSubmissionState(
		string slot,
		string id,
		IReadOnlyList<string> attachmentIds,
		string status,
		string error) =>
		_bridge.PostToWeb(JsonSerializer.Serialize(new { type = "agent-submission-state", slot, id, attachmentIds, status, error }));
}
