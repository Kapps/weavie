using Weavie.Core.Editor;

namespace Weavie.Hosting;

public sealed partial class HostSession {
	private sealed record ClientFileOpen(string Path, bool Preview);
	private sealed record CommitFileOpensMessage(ClientFileOpen[] Files, string? ActivePath, string OriginPageEpoch);

	private bool CommitFileOpens(CommitFileOpensMessage message) {
		ArgumentException.ThrowIfNullOrEmpty(message.OriginPageEpoch);
		foreach (var file in message.Files) {
			if (!Path.IsPathRooted(file.Path) || !FileProvider.CanRead(file.Path)) {
				throw new IOException($"Couldn't open {Path.GetFileName(file.Path)} — it's missing or unreadable.");
			}
			if (file.Preview && message.ActivePath is null) {
				throw new ArgumentException("An inactive file open cannot replace the preview tab.");
			}
		}
		if (message.ActivePath is { } active
			&& !message.Files.Any(file => SameEditorPath(file.Path, active))) {
			throw new ArgumentException("The active file must belong to this open operation.");
		}

		EditorSession next;
		lock (_editorSessionGate) {
			string? activePath = message.ActivePath ?? _editorSession.Active;
			foreach (var file in message.Files) {
				RecordEditorOpenLocked(file.Path, file.Preview, scratch: false, kind: null);
			}
			next = _editorSession with { Active = activePath };
			_editorSession = next;
			_editorMessages.Publish("filesOpened", new {
				paths = message.Files.Select(file => file.Path).ToArray(),
				originPageEpoch = message.OriginPageEpoch,
			});
		}
		EditorSessionChanged?.Invoke(next);
		return true;
	}
}
