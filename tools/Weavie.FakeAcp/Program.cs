using System.Text.Json;
using System.Text.Json.Nodes;
using Weavie.FakeAcp;

if (Environment.GetEnvironmentVariable("WEAVIE_FAKE_ACP_MODE") == "immediate-malformed") {
	Console.Out.WriteLine("{");
	Console.Out.Flush();
	await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
	return;
}

if (Environment.GetEnvironmentVariable("WEAVIE_FAKE_ACP_MODE") == "mixed-request-ids") {
	await MixedRequestIdFake.RunAsync().ConfigureAwait(false);
	return;
}

if (args is ["inference", var inferenceVariant]) {
	await InferenceFake.RunAsync(inferenceVariant).ConfigureAwait(false);
	return;
}

if (args is ["echo-and-exit"]) {
	string line = await Console.In.ReadLineAsync().ConfigureAwait(false)
		?? throw new EndOfStreamException("The echo fake expected one request.");
	using var document = JsonDocument.Parse(line);
	var request = document.RootElement;
	var response = new JsonObject {
		["id"] = JsonNode.Parse(request.GetProperty("id").GetRawText()),
		["result"] = new JsonObject { ["value"] = "final" },
	};
	if (request.TryGetProperty("jsonrpc", out _)) response["jsonrpc"] = "2.0";
	Console.Out.WriteLine(response.ToJsonString());
	Console.Out.Flush();
	Console.Error.WriteLine("final stderr");
	Console.Error.Flush();
	return;
}

if (args is ["malformed-error"]) {
	string line = await Console.In.ReadLineAsync().ConfigureAwait(false)
		?? throw new EndOfStreamException("The malformed-error fake expected one request.");
	using var document = JsonDocument.Parse(line);
	var response = new JsonObject {
		["jsonrpc"] = "2.0",
		["id"] = JsonNode.Parse(document.RootElement.GetProperty("id").GetRawText()),
		["error"] = new JsonObject { ["message"] = "missing code" },
	};
	Console.Out.WriteLine(response.ToJsonString());
	Console.Out.Flush();
	await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
	return;
}

if (args is ["terminal-output"]) {
	Console.Out.Write(new string('o', 16_384) + "stdout-tail");
	Console.Error.Write(new string('e', 16_384) + "stderr-tail");
	return;
}

if (args is ["terminal-hold"]) {
	await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
	return;
}

if (args is ["stdin-stall", var marker]) {
	char[] buffer = new char[4096];
	int received = 0;
	while (received < 32 * 1024) {
		int read = await Console.In.ReadAsync(buffer).ConfigureAwait(false);
		if (read == 0) throw new EndOfStreamException("The stdin-stall fake expected a large request.");
		received += read;
	}
	File.WriteAllText(marker, string.Empty);
	await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
	return;
}

var agent = new FakeAcpAgent();
var server = new AcpAgentServer(agent, Console.In, Console.Out, Console.Error);
await server.RunAsync(CancellationToken.None).ConfigureAwait(false);
