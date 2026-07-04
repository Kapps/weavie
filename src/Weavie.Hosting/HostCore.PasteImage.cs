using System.Text.Json;
using Weavie.Core.Editor;
using Weavie.Core.Json;

namespace Weavie.Hosting;

// Remote image paste into Claude: the web captures a pasted image and sends its bytes here; the host writes a
// scratch file on the backend's disk and injects the path into the claude PTY as a bracketed paste, which the TUI
// renders as an [Image #N] chip and attaches on submit. Claude ingests images only from the OS clipboard (absent
// on a remote/headless box) or a file path, so path-injection is the delivery that works everywhere. See
// docs/specs/remote-paste-image.md.
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
				$"That image is {approxBytes / (1024.0 * 1024.0):0.0} MB — Claude accepts images up to {PastedImageMedia.MaxBytes / (1024 * 1024)} MB. Resize it and paste again.");
			return;
		}

		// Bad base64 throws out to the OnWebMessage backstop (logged, not fatal), matching term-input.
		byte[] bytes = Convert.FromBase64String(dataB64);
		if (bytes.Length == 0) {
			return;
		}

		// The web only pastes into the claude pane; always target claude (a shell would try to run the path).
		if (SessionForSlot(root) is not { } session) {
			return;
		}

		string path = session.PastedImages.Write(extension, bytes);
		session.Claude.WriteBracketedPaste(path);
	}
}
