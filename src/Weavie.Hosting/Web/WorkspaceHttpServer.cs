using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Weavie.Hosting.Web;

/// <summary>The authenticated workspace origin shared by Windows, macOS, Linux, and Headless.</summary>
public sealed class WorkspaceHttpServer : IAsyncDisposable {
	private readonly HostCore _core;
	private readonly WorkspaceHttpServerOptions _options;
	private readonly IWorkspaceWebSocketBridge _bridge;
	private readonly WorkspaceMediaRoutes _media;
	private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private WebApplication? _app;
	private PhysicalFileProvider? _assets;
	private bool _ready;

	/// <summary>Creates one server for one HostCore workspace.</summary>
	public WorkspaceHttpServer(
		HostCore core,
		WorkspaceHttpServerOptions options,
		IWorkspaceWebSocketBridge bridge,
		WorkspaceMediaRoutes media) {
		ArgumentNullException.ThrowIfNull(core);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(bridge);
		ArgumentNullException.ThrowIfNull(media);
		_core = core;
		_options = options;
		_bridge = bridge;
		_media = media;
	}

	/// <summary>The bound origin after <see cref="StartAsync"/> completes.</summary>
	public string Origin { get; private set; } = string.Empty;

	/// <summary>The token-gated workspace document URI after the server is bound.</summary>
	public string PageUrl => $"{Origin}/index.html?token={Uri.EscapeDataString(_options.Token)}";

	/// <summary>The token-gated base URL the web app extends with session/path/revision query parameters.</summary>
	public string MediaBaseUrl => $"{Origin}/weavie-media?token={Uri.EscapeDataString(_options.Token)}";

	/// <summary>Binds Kestrel and begins accepting authenticated requests.</summary>
	public async Task StartAsync() {
		if (_app is not null) {
			return;
		}

		var builder = WebApplication.CreateBuilder();
		builder.Logging.ClearProviders();
		string bindHost = _options.BindAddress.Contains(":", StringComparison.Ordinal)
			&& !_options.BindAddress.StartsWith("[", StringComparison.Ordinal)
			? $"[{_options.BindAddress}]"
			: _options.BindAddress;
		builder.WebHost.UseUrls($"http://{bindHost}:{_options.Port}");
		var app = builder.Build();
		_app = app;
		app.UseWebSockets(new WebSocketOptions {
			KeepAliveInterval = TimeSpan.FromSeconds(30),
			KeepAliveTimeout = TimeSpan.FromSeconds(30),
		});

		_assets = Directory.Exists(_options.WebRoot)
			? new PhysicalFileProvider(_options.WebRoot)
			: null;
		var assets = _assets;
		app.Use(async (context, next) => {
			var path = context.Request.Path;
			bool publicAsset = assets is not null
				&& path != "/" && path != "/index.html"
				&& assets.GetFileInfo(path.Value ?? "/").Exists;
			if (publicAsset || TokenMatches(context, _options.Token)) {
				await next().ConfigureAwait(false);
				return;
			}

			context.Response.StatusCode = StatusCodes.Status401Unauthorized;
		});
		app.Use(async (context, next) => {
			if (Volatile.Read(ref _ready)) {
				await next().ConfigureAwait(false);
				return;
			}

			context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
			await context.Response.WriteAsync("weavie is starting").ConfigureAwait(false);
		});
		app.MapMethods("/weavie-media", [HttpMethods.Get, HttpMethods.Head], ServeMediaAsync);
		if (_bridge.Available) {
			app.Map("/weavie-bridge", ServeBridgeAsync);
		}

		if (_options.EnableControl) {
			app.MapGet("/control/status", () => Results.Json(new {
				buildNumber = HostCore.BuildNumber,
				draining = _core.Draining,
			}));
			app.MapPost("/control/drain", () => {
				_core.BeginDrain(app.Lifetime.StopApplication);
				return Results.Accepted();
			});
		}

		app.Use(async (context, next) => {
			string path = context.Request.Path.Value ?? "/";
			if (path is "/" or "/index.html") {
				await ServeIndexAsync(context).ConfigureAwait(false);
				return;
			}

			await next().ConfigureAwait(false);
		});
		if (assets is not null) {
			app.UseStaticFiles(new StaticFileOptions { FileProvider = assets });
		}

		await app.StartAsync().ConfigureAwait(false);
		Origin = NormalizeOrigin(app.Urls.First(), _options.BindAddress);
		app.Lifetime.ApplicationStopped.Register(_stopped.SetResult);
	}

	/// <summary>Marks the Core graph ready for dynamic requests.</summary>
	public void MarkReady() => Volatile.Write(ref _ready, true);

	/// <summary>Waits until Kestrel is asked to shut down.</summary>
	public Task WaitForShutdownAsync() => _app is null
		? throw new InvalidOperationException("The workspace server has not started.")
		: _stopped.Task;

	private async Task ServeMediaAsync(HttpContext context) {
		string session = context.Request.Query["session"].ToString();
		string path = context.Request.Query["path"].ToString();
		var resource = _media.Open(session, path);
		if (resource is null) {
			context.Response.StatusCode = StatusCodes.Status404NotFound;
			return;
		}

		context.Response.Headers.CacheControl = "private, no-cache";
		context.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
		context.Response.Headers["Referrer-Policy"] = "no-referrer";
		string tag = $"\"{resource.LastModified.ToUnixTimeMilliseconds():x}-{resource.Length:x}\"";
		var result = Results.File(
			resource.Stream,
			resource.ContentType,
			fileDownloadName: null,
			resource.LastModified,
			new EntityTagHeaderValue(tag, isWeak: true),
			enableRangeProcessing: true);
		await result.ExecuteAsync(context).ConfigureAwait(false);
	}

	private async Task ServeBridgeAsync(HttpContext context) {
		if (!context.WebSockets.IsWebSocketRequest) {
			context.Response.StatusCode = StatusCodes.Status400BadRequest;
			return;
		}

		using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
		using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
			context.RequestAborted,
			_app!.Lifetime.ApplicationStopping);
		await _bridge.ServeAsync(socket, lifetime.Token).ConfigureAwait(false);
	}

	private async Task ServeIndexAsync(HttpContext context) {
		context.Response.ContentType = "text/html; charset=utf-8";
		context.Response.Headers["Referrer-Policy"] = "no-referrer";
		string indexPath = Path.Combine(_options.WebRoot, "index.html");
		if (!File.Exists(indexPath)) {
			await context.Response.WriteAsync(
				"<!doctype html><meta charset=utf-8><body><h1>Weavie web assets not found</h1>")
				.ConfigureAwait(false);
			return;
		}

		string html;
		try {
			html = await File.ReadAllTextAsync(indexPath, Encoding.UTF8, context.RequestAborted).ConfigureAwait(false);
		} catch (IOException ex) {
			context.Response.StatusCode = StatusCodes.Status500InternalServerError;
			await context.Response.WriteAsync($"failed to read index.html: {ex.Message}").ConfigureAwait(false);
			return;
		}

		string bridge = _bridge.Available ? "window.__WEAVIE_BRIDGE_WS__ = \"auto\";" : string.Empty;
		string resourceBase = JsonSerializer.Serialize(
			$"/weavie-media?token={Uri.EscapeDataString(_options.Token)}");
		string bootstrap =
			$"<script>{bridge}{_core.BuildBootstrap()}window.__WEAVIE_RESOURCE_BASE__ = {resourceBase};</script>";
		html = html.Contains("<head>", StringComparison.Ordinal)
			? html.Replace("<head>", "<head>" + bootstrap, StringComparison.Ordinal)
			: bootstrap + html;
		await context.Response.WriteAsync(html).ConfigureAwait(false);
	}

	private static bool TokenMatches(HttpContext context, string expected) {
		string presented = context.Request.Query.TryGetValue("token", out var token)
			? token.ToString()
			: string.Empty;
		if (presented.Length != expected.Length) {
			return false;
		}

		int diff = 0;
		for (int i = 0; i < expected.Length; i++) {
			diff |= presented[i] ^ expected[i];
		}

		return diff == 0;
	}

	private static string NormalizeOrigin(string bound, string requestedBind) {
		var uri = new Uri(bound);
		string host = requestedBind is "0.0.0.0" or "::" or "[::]" ? "127.0.0.1" : uri.Host;
		return $"http://{host}:{uri.Port}";
	}

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		if (_app is null) {
			return;
		}

		await _app.StopAsync().ConfigureAwait(false);
		await _app.DisposeAsync().ConfigureAwait(false);
		_app = null;
		_assets?.Dispose();
		_assets = null;
	}
}
