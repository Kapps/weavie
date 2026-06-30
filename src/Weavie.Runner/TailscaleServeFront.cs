using System.Text.Json;

namespace Weavie.Runner;

/// <summary>
/// Terminates TLS via <c>tailscale serve</c>: tailscaled fronts the loopback control plane and worker with the
/// node's publicly-trusted <c>*.ts.net</c> certificate, so a secure-origin browser opens <c>wss://</c> with no
/// cert install. It discovers the node's MagicDNS name, maps the two https front ports at construction, and
/// clears them on dispose. Because the worker port is fixed in secured modes, both mappings survive worker
/// restarts. Fails loudly when tailscale is missing, logged out, or HTTPS isn't enabled for the tailnet.
/// See docs/specs/tls-on-the-runner.md.
/// </summary>
internal sealed class TailscaleServeFront : ITlsFront {
	private readonly RunnerOptions _options;
	private readonly ITailscaleCli _cli;
	private readonly Action<string>? _log;
	private readonly string _magicDns;
	private readonly int _workerPort;

	/// <summary>
	/// Discovers the node's MagicDNS name and maps the control + worker https ports through <c>tailscale serve</c>.
	/// Throws (so the runner exits) when tailscale can't be driven or the tailnet isn't HTTPS-capable.
	/// </summary>
	public TailscaleServeFront(RunnerOptions options, ITailscaleCli cli, Action<string>? log) {
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(cli);
		_options = options;
		_cli = cli;
		_log = log;
		_workerPort = options.WorkerPort
			?? throw new InvalidOperationException("tailscale mode needs a fixed worker port (set by RunnerOptions).");

		_magicDns = DiscoverMagicDns();
		Serve(_options.ControlHttpsPort, _options.Port);
		try {
			Serve(_options.WorkerHttpsPort, _workerPort);
		} catch {
			ClearServe(_options.ControlHttpsPort);
			throw;
		}
	}

	/// <inheritdoc/>
	public string ControlBindAddress => "127.0.0.1";

	/// <inheritdoc/>
	public string WorkerBindAddress => "127.0.0.1";

	/// <inheritdoc/>
	public string RegisterUrl => $"https://{TlsUrls.HostPort(_magicDns, _options.ControlHttpsPort)}";

	/// <inheritdoc/>
	public string WorkerPageUrl(string requestHost, WorkspaceBackend backend) {
		ArgumentNullException.ThrowIfNull(backend);
		return $"https://{TlsUrls.HostPort(_magicDns, _options.WorkerHttpsPort)}/?token={backend.Token}";
	}

	/// <inheritdoc/>
	public ValueTask DisposeAsync() {
		ClearServe(_options.ControlHttpsPort);
		ClearServe(_options.WorkerHttpsPort);
		return ValueTask.CompletedTask;
	}

	private string DiscoverMagicDns() {
		var result = _cli.Run(["status", "--json"]);
		if (result.ExitCode != 0) {
			throw new InvalidOperationException($"tailscale status failed (exit {result.ExitCode}): {Hint(result.Stderr)}");
		}

		string? dnsName;
		try {
			using var doc = JsonDocument.Parse(result.Stdout);
			dnsName = doc.RootElement.TryGetProperty("Self", out var self) && self.TryGetProperty("DNSName", out var dns)
				? dns.GetString()
				: null;
		} catch (JsonException ex) {
			throw new InvalidOperationException("could not parse 'tailscale status --json' output.", ex);
		}

		if (string.IsNullOrEmpty(dnsName)) {
			throw new InvalidOperationException(
				"tailscale reported no MagicDNS name for this node — is it logged in with MagicDNS + HTTPS enabled for the tailnet?");
		}

		return dnsName.TrimEnd('.');
	}

	private void Serve(int httpsPort, int loopbackPort) {
		var result = _cli.Run(["serve", "--bg", $"--https={httpsPort}", $"http://127.0.0.1:{loopbackPort}"]);
		if (result.ExitCode != 0) {
			throw new InvalidOperationException(
				$"tailscale serve --https={httpsPort} failed (exit {result.ExitCode}): {Hint(result.Stderr)}");
		}

		_log?.Invoke($"serve https://{_magicDns}:{httpsPort} -> 127.0.0.1:{loopbackPort}");
	}

	private void ClearServe(int httpsPort) {
		var result = _cli.Run(["serve", $"--https={httpsPort}", "off"]);
		if (result.ExitCode != 0) {
			_log?.Invoke($"serve teardown for :{httpsPort} failed (exit {result.ExitCode}): {Hint(result.Stderr)}");
		}
	}

	private static string Hint(string stderr) {
		string trimmed = stderr.Trim();
		return trimmed.Length == 0 ? "(no error output)" : trimmed;
	}
}
