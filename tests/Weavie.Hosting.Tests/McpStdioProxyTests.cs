using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Weavie.Hosting.Web;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class McpStdioProxyTests {
	[Fact]
	public async Task ProxyDispatchesRequestsWithoutHeadOfLineBlocking() {
		var requests = Channel.CreateUnbounded<TaskCompletionSource<string>>();
		await using var server = await StartServerAsync(async context => {
			_ = await new StreamReader(context.Request.Body).ReadToEndAsync();
			var response = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
			await requests.Writer.WriteAsync(response);
			string json = await response.Task.WaitAsync(context.RequestAborted);
			context.Response.ContentType = "application/json";
			await context.Response.WriteAsync(json);
		});
		await WithProxyAsync(server, async process => {
			await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"openDiff\"}");
			var first = await requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
			await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");
			var second = await requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
			second.SetResult("{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{}}");
			Assert.Contains("\"id\":2", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
			first.SetResult("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}");
			Assert.Contains("\"id\":1", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
		});
	}

	[Fact]
	public async Task ProxyCancelsHeldHttpRequestsWhenItsOwnerClosesStdin() {
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		await using var server = await StartServerAsync(async context => {
			_ = await new StreamReader(context.Request.Body).ReadToEndAsync();
			using var registration = context.RequestAborted.Register(() => canceled.TrySetResult());
			entered.TrySetResult();
			await canceled.Task;
		});
		await WithProxyAsync(server, async process => {
			await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"openDiff\"}");
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			process.StandardInput.Close();
			await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
		});
	}

	private static async Task<WebApplication> StartServerAsync(RequestDelegate handler) {
		var builder = WorkspaceHttpServer.CreateApplicationBuilder();
		builder.WebHost.UseUrls("http://127.0.0.1:0");
		var app = builder.Build();
		app.Run(handler);
		await app.StartAsync();
		return app;
	}

	private static async Task WithProxyAsync(WebApplication server, Func<Process, Task> exercise) {
		using var process = StartProxy(Assert.Single(server.Urls));
		try {
			await exercise(process);
		} finally {
			process.StandardInput.Close();
			try {
				await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
			} finally {
				if (!process.HasExited) {
					process.Kill(entireProcessTree: true);
					await process.WaitForExitAsync();
				}
			}
		}
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

}
