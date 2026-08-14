using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

internal static class McpStdioProxy {
	private const string SessionHeader = "Mcp-Session-Id";

	public static async Task<int> RunAsync() {
		string url = RequiredEnvironment("WEAVIE_MCP_URL");
		string token = RequiredEnvironment("WEAVIE_MCP_TOKEN");
		using var client = new HttpClient();
		using var cancellation = new CancellationTokenSource();
		using var outputGate = new SemaphoreSlim(1, 1);
		var session = new McpSession();
		var requests = new ConcurrentDictionary<Task, byte>();
		var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		while (true) {
			var read = Console.In.ReadLineAsync(cancellation.Token).AsTask();
			var completed = await Task.WhenAny(read, failed.Task).ConfigureAwait(false);
			if (completed == failed.Task) await failed.Task.ConfigureAwait(false);
			string? message = await read.ConfigureAwait(false);
			if (message is null) {
				cancellation.Cancel();
				try {
					await Task.WhenAll(requests.Keys).ConfigureAwait(false);
				} catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
				}
				return 0;
			}
			if (message.Length == 0) continue;
			var request = RelayAsync(client, url, token, message, session, outputGate, cancellation.Token);
			requests.TryAdd(request, 0);
			_ = request.ContinueWith(
				faulted => {
					requests.TryRemove(faulted, out _);
					var error = faulted.Exception?.InnerException
						?? new InvalidOperationException("An MCP proxy request failed.");
					if (failed.TrySetException(error)) cancellation.Cancel();
				},
				CancellationToken.None,
				TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
				TaskScheduler.Default);
			_ = request.ContinueWith(
				completed => requests.TryRemove(completed, out _),
				CancellationToken.None,
				TaskContinuationOptions.NotOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
				TaskScheduler.Default);
		}
	}

	private static async Task RelayAsync(
		HttpClient client,
		string url,
		string token,
		string message,
		McpSession session,
		SemaphoreSlim outputGate,
		CancellationToken cancellationToken) {
		using var request = new HttpRequestMessage(HttpMethod.Post, url) {
			Content = new StringContent(message, Encoding.UTF8, "application/json"),
		};
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
		if (session.Id is { } sessionId) {
			request.Headers.TryAddWithoutValidation(SessionHeader, sessionId);
		}
		using var response = await client.SendAsync(
			request,
			HttpCompletionOption.ResponseHeadersRead,
			cancellationToken).ConfigureAwait(false);
		if (response.Headers.TryGetValues(SessionHeader, out var values)) {
			session.Adopt(values.Single());
		}
		if (response.StatusCode == HttpStatusCode.Accepted) return;
		response.EnsureSuccessStatusCode();
		if (response.Content.Headers.ContentType?.MediaType == "text/event-stream") {
			await RelayEventsAsync(response, outputGate, cancellationToken).ConfigureAwait(false);
		} else {
			string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			if (body.Length > 0) await WriteAsync(body, outputGate, cancellationToken).ConfigureAwait(false);
		}
	}

	private static async Task RelayEventsAsync(
		HttpResponseMessage response,
		SemaphoreSlim outputGate,
		CancellationToken cancellationToken) {
		using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		using var reader = new StreamReader(stream, Encoding.UTF8);
		while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line) {
			if (line.StartsWith("data:", StringComparison.Ordinal)) {
				await WriteAsync(line[5..].TrimStart(), outputGate, cancellationToken).ConfigureAwait(false);
			}
		}
	}

	private static async Task WriteAsync(
		string message,
		SemaphoreSlim outputGate,
		CancellationToken cancellationToken) {
		await outputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try {
			await Console.Out.WriteLineAsync(message).ConfigureAwait(false);
			await Console.Out.FlushAsync(cancellationToken).ConfigureAwait(false);
		} finally {
			outputGate.Release();
		}
	}

	private static string RequiredEnvironment(string name) =>
		Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
			? value
			: throw new InvalidOperationException($"The MCP proxy requires {name}.");

	private sealed class McpSession {
		private readonly object _gate = new();
		private string? _id;

		public string? Id {
			get { lock (_gate) return _id; }
		}

		public void Adopt(string id) {
			lock (_gate) {
				if (_id is not null && !string.Equals(_id, id, StringComparison.Ordinal)) {
					throw new InvalidOperationException("The MCP server changed its session id.");
				}
				_id = id;
			}
		}
	}
}
