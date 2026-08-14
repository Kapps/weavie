using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class McpStdioProxyTests {
	[Fact]
	public async Task ProxyDispatchesRequestsWithoutHeadOfLineBlocking() {
		int port = FreePort();
		string url = $"http://127.0.0.1:{port}/";
		using var listener = new HttpListener();
		listener.Prefixes.Add(url);
		listener.Start();
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

	[Fact]
	public async Task ProxyCancelsHeldHttpRequestsWhenItsOwnerClosesStdin() {
		int port = FreePort();
		string url = $"http://127.0.0.1:{port}/";
		using var listener = new HttpListener();
		listener.Prefixes.Add(url);
		listener.Start();
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

	private static int FreePort() {
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		return ((IPEndPoint)listener.LocalEndpoint).Port;
	}
}
