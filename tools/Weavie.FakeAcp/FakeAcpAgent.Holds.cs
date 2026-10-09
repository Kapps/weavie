using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weavie.FakeAcp;

// File-signalled holds that let a test catch the client with work in flight.
internal sealed partial class FakeAcpAgent {
	// A test opts into holding the next opening by creating hold-open; the hold consumes it.
	private async Task<JsonObject> OpenAsync(JsonElement parameters, string sessionId, bool replay, CancellationToken ct) {
		await HoldOnceAsync("open", ct).ConfigureAwait(false);
		return Open(parameters, sessionId, replay);
	}

	private static async Task HoldOnceAsync(string name, CancellationToken ct) {
		string hold = Path.Combine(Environment.CurrentDirectory, "hold-" + name);
		if (!File.Exists(hold)) return;
		File.Delete(hold);
		File.WriteAllText(Path.Combine(Environment.CurrentDirectory, name + "-started"), string.Empty);
		while (!File.Exists(Path.Combine(Environment.CurrentDirectory, "release-" + name))) await Task.Delay(10, ct).ConfigureAwait(false);
	}

	// Records how the client ended a terminal wait it was still serving, so a test can prove it was answered.
	private async Task TerminalWaitAsync(CancellationToken ct) {
		var created = await Connection().RequestAsync("terminal/create", new JsonObject {
			["sessionId"] = _sessionId,
			["command"] = Environment.ProcessPath,
			["args"] = new JsonArray("terminal-hold"),
		}, ct).ConfigureAwait(false);
		var waiting = Connection().RequestAsync("terminal/wait_for_exit", new JsonObject {
			["sessionId"] = _sessionId,
			["terminalId"] = AcpJson.RequiredString(created, "terminalId", "terminal/create response"),
		}, ct);
		File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "terminal-wait-started"), string.Empty);
		string outcome;
		try {
			await waiting.ConfigureAwait(false);
			outcome = "exited";
		} catch (AcpAdapterException error) {
			outcome = error.Message;
		}
		File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "terminal-wait-answered"), outcome);
	}
}
