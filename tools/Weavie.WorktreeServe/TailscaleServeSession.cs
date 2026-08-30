using System.Diagnostics;
using System.Text.Json;
using Weavie.Core.Remote;

namespace Weavie.WorktreeServe;

internal sealed class TailscaleServeSession : IDisposable {
	private readonly ITailscaleCli _cli;
	private readonly string _magicDns;
	private readonly int _httpsPort;
	private readonly string _target;
	private readonly SupervisedProcess _process;

	public TailscaleServeSession(ITailscaleCli cli, string magicDns, int httpsPort, string target) {
		_cli = cli;
		_magicDns = magicDns;
		_httpsPort = httpsPort;
		_target = target;
		var info = new ProcessStartInfo(cli.Executable) {
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		foreach (string arg in Arguments(httpsPort, target)) {
			info.ArgumentList.Add(arg);
		}
		foreach (var (name, value) in cli.ProcessEnvironment) {
			info.Environment[name] = value;
		}

		_process = new SupervisedProcess(
			"tailscale-serve",
			info,
			line => Console.WriteLine($"[tailscale] {line}"),
			line => Console.Error.WriteLine($"[tailscale] {line}"));
	}

	public Task<int> Completion => _process.Completion;

	public static IReadOnlyList<string> Arguments(int httpsPort, string target) =>
		["serve", "--bg=false", $"--https={httpsPort}", target];

	public static string DiscoverMagicDns(ITailscaleCli cli) {
		ArgumentNullException.ThrowIfNull(cli);
		var result = cli.Run(["status", "--json"]);
		if (result.ExitCode != 0) {
			throw new InvalidOperationException($"tailscale status failed (exit {result.ExitCode}): {Hint(result.Stderr)}");
		}

		try {
			using var document = JsonDocument.Parse(result.Stdout);
			string? name = document.RootElement.TryGetProperty("Self", out var self)
				&& self.TryGetProperty("DNSName", out var dns)
				? dns.GetString()
				: null;
			return string.IsNullOrEmpty(name)
				? throw new InvalidOperationException("tailscale reported no MagicDNS name for this node.")
				: name.TrimEnd('.');
		} catch (JsonException ex) {
			throw new InvalidOperationException("could not parse 'tailscale status --json' output.", ex);
		}
	}

	public static TailscaleServeStatus ReadStatus(ITailscaleCli cli) {
		var result = cli.Run(["serve", "status", "--json"]);
		if (result.ExitCode != 0) {
			throw new InvalidOperationException(
				$"tailscale serve status failed (exit {result.ExitCode}): {Hint(result.Stderr)}");
		}

		return TailscaleServeStatus.Parse(result.Stdout);
	}

	public async Task StartAsync(CancellationToken cancellationToken) {
		_process.Start();
		while (true) {
			cancellationToken.ThrowIfCancellationRequested();
			var status = ReadStatus(_cli);
			if (status.IsExactHttpsProxy(_magicDns, _httpsPort, _target)) {
				return;
			}

			if (status.PortIsOccupied(_httpsPort)) {
				throw new InvalidOperationException(
					$"Tailscale HTTPS port {_httpsPort} changed ownership while the preview was starting; leaving it untouched.");
			}

			var delay = Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
			var completed = await Task.WhenAny(_process.Completion, delay).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			if (completed == _process.Completion) {
				int exitCode = await _process.Completion.ConfigureAwait(false);
				throw new InvalidOperationException($"foreground tailscale serve exited before its route was ready (code {exitCode}).");
			}
		}
	}

	public async Task StopAndVerifyAsync() {
		_process.Stop();
		await _process.Completion.ConfigureAwait(false);
		var status = ReadStatus(_cli);
		if (status.IsExactHttpsProxy(_magicDns, _httpsPort, _target)) {
			throw new InvalidOperationException(
				$"foreground tailscale serve exited but its exact HTTPS route on {_httpsPort} remained; no shared configuration was changed.");
		}
	}

	public void Dispose() => _process.Dispose();

	private static string Hint(string stderr) =>
		stderr.Trim() is { Length: > 0 } hint ? hint : "(no error output)";
}
