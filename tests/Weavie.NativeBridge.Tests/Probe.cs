using System.Text.Json;

namespace Weavie.NativeBridge.Tests;

internal sealed class Probe(string appUrl, string foreignUrl, string scenario, string profileDirectory) {
	private bool _ready;
	private bool _loaded;
	private bool _finished;
	internal int ExitCode { get; private set; } = 1;
	internal string AppUrl => appUrl;
	internal string ProfileDirectory => profileDirectory;
	internal bool Popup => scenario == "popup";
	internal bool Started { get; private set; }
	internal event Action<string>? Execute;
	internal event Action<string>? Navigate;

	internal void Receive(string message) {
		if (message != "ready") {
			Fail("An unauthorized document reached the native bridge: " + message);
			return;
		}
		_ready = true;
		Console.WriteLine("Authenticated initial document ready");
		Start();
	}

	internal void Loaded() {
		_loaded = true;
		Console.WriteLine("Native document load finished");
		Start();
	}

	private void Start() {
		if (!_ready || !_loaded || Started) return;
		Started = true;
		string target = scenario switch {
			"foreign" or "popup" => foreignUrl + "/untrusted",
			"redirect" => foreignUrl + "/redirect-untrusted",
			"data" => "data:text/html,untrusted",
			"app-frame" => appUrl,
			"redirect-app-frame" => foreignUrl + "/redirect-app?target=" + Uri.EscapeDataString(appUrl),
			_ => throw new ArgumentException("Unknown native probe: " + scenario),
		};
		string literal = $"\"{JsonEncodedText.Encode(target)}\"";
		Console.WriteLine("Attempting " + scenario + ": " + target);
		// Chromium blocks renderer-initiated data navigation before native policy observes it.
		if (scenario == "data") {
			Navigate?.Invoke(target);
			return;
		}
		Execute?.Invoke(scenario switch {
			"popup" => $"const link = document.createElement('a'); link.href = {literal}; link.target = '_blank'; document.body.append(link); link.click();",
			"app-frame" or "redirect-app-frame" => $"const frame = document.createElement('iframe'); frame.src = {literal}; document.body.append(frame);",
			_ => $"location.href = {literal};",
		});
	}

	internal void Denied() {
		if (!Started) {
			Fail("Native policy rejected the trusted initial document");
			return;
		}
		Finish(0, "PASS: " + scenario + " rejected by native policy");
	}

	internal void Fail(string message) => Finish(1, "FAIL: " + message);

	private void Finish(int code, string message) {
		if (_finished) return;
		_finished = true;
		ExitCode = code;
		Console.WriteLine(message);
		Console.Out.Flush();
	}
}

internal static class Program {
	[STAThread]
	private static int Main(string[] args) {
		var probe = new Probe(args[0], args[1], args[2], args[3]);
		return NativeProbe.Run(probe);
	}
}
