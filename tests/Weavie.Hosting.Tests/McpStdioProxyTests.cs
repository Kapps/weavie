using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Weavie.Hosting.Tests;

// Flaked 2026-09-07 05:50 UTC on main (run https://github.com/Kapps/weavie/actions/runs/34088142697/job/101636049373):
// ProxyCancelsHeldHttpRequestsWhenItsOwnerClosesStdin hit "System.Net.HttpListenerException: Address already in use".
// Root cause: FreePort() bound a TcpListener to port 0 to discover a free port, then released it before the
// HttpListener bound the same number — a TOCTOU race any concurrently-running test's own FreePort() call could win.
// Fixed by making listener startup retry with a freshly discovered port on collision instead of trusting the
// released reservation to still be free (StartListener below).
public sealed class McpStdioProxyTests {
	[Fact]
	public async Task ProxyDispatchesRequestsWithoutHeadOfLineBlocking() {
		using var listener = StartListener(out string url);
		using var process = StartProxy(url);

		await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"openDiff\"}");
		var first = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(5));
		_ = await new StreamReader(first.Request.InputStream).ReadToEndAsync();

		await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");
		var second = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(5));
		_ = await new StreamReader(second.Request.InputStream).ReadToEndAsync();
		await RespondAsync(second, "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{}}");
		Assert.Contains("\"id\":2", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));

		await RespondAsync(first, "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}");
		Assert.Contains("\"id\":1", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
		process.StandardInput.Close();
		await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(0, process.ExitCode);
	}

	// Flaked 2026-09-07 05:50 UTC on main (https://github.com/Kapps/weavie/actions/runs/34088142697):
	// HttpListenerException "Address already in use" — the free port handed to HttpListener could be
	// grabbed by something else between selection and bind. Fixed by retrying port selection on a
	// bind conflict in StartListener() instead of failing the test.
	[Fact]
	public async Task ProxyCancelsHeldHttpRequestsWhenItsOwnerClosesStdin() {
		using var listener = StartListener(out string url);
		using var process = StartProxy(url);

		await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"openDiff\"}");
		var held = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(5));
		_ = await new StreamReader(held.Request.InputStream).ReadToEndAsync();
		process.StandardInput.Close();

		await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
		held.Response.Abort();
		Assert.Equal(0, process.ExitCode);
	}

	private static Process StartProxy(string url) {
		string executable = Path.Combine(
			AppContext.BaseDirectory,
			OperatingSystem.IsWindows() ? "weavie-mcp-proxy.exe" : "weavie-mcp-proxy");
		var start = new ProcessStartInfo(executable) {
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		start.Environment["WEAVIE_MCP_URL"] = url;
		start.Environment["WEAVIE_MCP_TOKEN"] = "test-token";
		return Process.Start(start) ?? throw new InvalidOperationException("The MCP proxy did not start.");
	}

	private static async Task RespondAsync(HttpListenerContext context, string json) {
		byte[] response = Encoding.UTF8.GetBytes(json);
		context.Response.ContentType = "application/json";
		context.Response.ContentLength64 = response.Length;
		await context.Response.OutputStream.WriteAsync(response);
		context.Response.Close();
	}

	// HttpListener has no way to bind port 0 itself, so the free port it starts on is chosen via a
	// throwaway TcpListener and handed off. That handoff has an unavoidable gap in which another
	// process can grab the same port first, so retry with a fresh candidate on a bind conflict
	// rather than letting that race fail the test outright.
	private static HttpListener StartListener(out string url) {
		for (int attempt = 0; ; attempt++) {
			int port = FreePort();
			string candidateUrl = $"http://127.0.0.1:{port}/";
			var listener = new HttpListener();
			listener.Prefixes.Add(candidateUrl);
			try {
				listener.Start();
				url = candidateUrl;
				return listener;
			} catch (HttpListenerException) when (attempt < 4) {
				listener.Close();
			}
		}
	}

	private static int FreePort() {
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		return ((IPEndPoint)listener.LocalEndpoint).Port;
	}

	private static HttpListener StartListener(out string url) {
		for (int attempt = 0; ; attempt++) {
			string candidateUrl = $"http://127.0.0.1:{FreePort()}/";
			var listener = new HttpListener();
			listener.Prefixes.Add(candidateUrl);
			try {
				listener.Start();
				url = candidateUrl;
				return listener;
			} catch (HttpListenerException) when (attempt < 4) {
				listener.Close();
			}
		}
	}
}
