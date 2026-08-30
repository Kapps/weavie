using System.Text.Json;

namespace Weavie.WorktreeServe;

internal sealed class TailscaleServeStatus {
	private readonly HashSet<int> _tcpPorts;
	private readonly HashSet<int> _webPorts;
	private readonly HashSet<int> _httpsPorts;
	private readonly HashSet<(string Host, string Target)> _rootProxies;

	private TailscaleServeStatus(
		HashSet<int> tcpPorts,
		HashSet<int> webPorts,
		HashSet<int> httpsPorts,
		HashSet<(string Host, string Target)> rootProxies) {
		_tcpPorts = tcpPorts;
		_webPorts = webPorts;
		_httpsPorts = httpsPorts;
		_rootProxies = rootProxies;
	}

	public static TailscaleServeStatus Parse(string json) {
		try {
			using var document = JsonDocument.Parse(json);
			var tcpPorts = new HashSet<int>();
			var webPorts = new HashSet<int>();
			var httpsPorts = new HashSet<int>();
			var rootProxies = new HashSet<(string Host, string Target)>();
			var root = document.RootElement;
			ReadScope(root, tcpPorts, webPorts, httpsPorts, rootProxies);
			if (root.TryGetProperty("Foreground", out var foreground)
				&& foreground.ValueKind == JsonValueKind.Object) {
				foreach (var session in foreground.EnumerateObject()) {
					ReadScope(session.Value, tcpPorts, webPorts, httpsPorts, rootProxies);
				}
			}

			return new TailscaleServeStatus(tcpPorts, webPorts, httpsPorts, rootProxies);
		} catch (JsonException ex) {
			throw new InvalidOperationException("could not parse 'tailscale serve status --json' output.", ex);
		}
	}

	public bool PortIsOccupied(int port) =>
		_tcpPorts.Contains(port) || _webPorts.Contains(port);

	public bool IsExactHttpsProxy(string magicDns, int port, string target) =>
		_httpsPorts.Contains(port)
		&& _rootProxies.Contains(($"{magicDns}:{port}", target));

	private static void ReadScope(
		JsonElement scope,
		HashSet<int> tcpPorts,
		HashSet<int> webPorts,
		HashSet<int> httpsPorts,
		HashSet<(string Host, string Target)> rootProxies) {
		if (scope.TryGetProperty("TCP", out var tcp) && tcp.ValueKind == JsonValueKind.Object) {
			foreach (var entry in tcp.EnumerateObject()) {
				if (int.TryParse(entry.Name, out int port)) {
					tcpPorts.Add(port);
					if (entry.Value.TryGetProperty("HTTPS", out var https)
						&& https.ValueKind == JsonValueKind.True) {
						httpsPorts.Add(port);
					}
				}
			}
		}

		if (scope.TryGetProperty("Web", out var web) && web.ValueKind == JsonValueKind.Object) {
			foreach (var entry in web.EnumerateObject()) {
				if (PortOf(entry.Name) is { } port) {
					webPorts.Add(port);
				}

				if (entry.Value.TryGetProperty("Handlers", out var handlers)
					&& handlers.TryGetProperty("/", out var rootHandler)
					&& rootHandler.TryGetProperty("Proxy", out var proxy)
					&& proxy.GetString() is { } target) {
					rootProxies.Add((entry.Name, target));
				}
			}
		}
	}

	private static int? PortOf(string hostPort) {
		int separator = hostPort.LastIndexOf(':');
		return separator >= 0 && int.TryParse(hostPort[(separator + 1)..], out int port) ? port : null;
	}
}
