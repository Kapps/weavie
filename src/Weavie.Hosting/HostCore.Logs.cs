using System.Net;
using System.Text.Json;
using Weavie.Core.Commands;
using Weavie.Core.Diagnostics;

namespace Weavie.Hosting;

// The in-app log viewer. `weavie.view.logs` snapshots the process's captured console output (the LogBuffer teed
// over Console at startup) and both (a) opens it as a read-only `about:logs` source tab for the user, and (b)
// returns a bounded tail as the command's data payload — so Claude can read errors over runCommand that would
// otherwise only surface in the terminal the app was launched from.
public sealed partial class HostCore {
	// The source-tab key for the diagnostics view; never fetched (the host fills its content directly).
	private const string LogsTarget = "about:logs";
	// How many of the most-recent lines the command hands back to Claude; the human tab shows the full buffer.
	private const int LogTailForClaude = 500;

	private CommandResult ShowLogs() {
		var (lines, dropped) = _logBuffer.Snapshot();
		string full = string.Join('\n', lines);

		// Human tab: the full buffer as a read-only source doc (SourceView re-sanitizes the html via DOMPurify). No
		// `text` — the web renders only `html`, and Claude's plaintext channel is the DataJson tail below, so
		// sending the whole buffer again as text would just be dead weight on the bridge.
		_bridge.PostToWeb(JsonSerializer.Serialize(new {
			type = "source-doc",
			target = LogsTarget,
			title = "Weavie Logs",
			html = LogsHtml(full, dropped),
		}));

		// Claude channel: the most-recent tail, with the omitted count surfaced so a truncation is never silent.
		int shown = Math.Min(lines.Count, LogTailForClaude);
		int omitted = dropped + (lines.Count - shown);
		string dataJson = JsonSerializer.Serialize(new {
			log = string.Join('\n', lines.Skip(lines.Count - shown)),
			shown,
			omitted,
		});
		string omittedNote = omitted > 0 ? $", {omitted} earlier omitted" : string.Empty;
		return CommandResult.Success($"Opened the Weavie logs ({shown} most-recent line(s){omittedNote}).", dataJson);
	}

	// Wraps the escaped log text in a <pre> (both tags are in SourceView's DOMPurify allowlist), prefixed with a
	// dropped-lines marker when the ring evicted earlier output — surfacing the bound where the reader sees it.
	private static string LogsHtml(string full, int dropped) {
		string marker = dropped > 0
			? $"<div class=\"wv-logs-note\">… {dropped} earlier line(s) dropped (showing the most recent {LogBuffer.DefaultCapacity}) …</div>"
			: string.Empty;
		return $"{marker}<pre>{WebUtility.HtmlEncode(full)}</pre>";
	}
}
