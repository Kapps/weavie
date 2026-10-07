using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Weavie.Hosting.Web;

namespace Weavie.Runner;

/// <summary>
/// The runner's browser entry and auth'd control plane. Browser cookies open only the root handoff; a
/// default-deny middleware requires an explicit bearer token everywhere else.
/// </summary>
internal static class ControlApi {
	public static void Map(WebApplication app, BackendManager backends, RunnerOptions options, ITlsFront front, Func<UpdateStatus> updateStatus) {
		ArgumentNullException.ThrowIfNull(updateStatus);
		var authentication = new RunnerAuthentication(options.RunnerToken);
		app.Use(async (context, next) => {
			if (IsBrowserEntry(context) || authentication.BearerMatches(context)) {
				await next().ConfigureAwait(false);
				return;
			}

			context.Response.StatusCode = StatusCodes.Status401Unauthorized;
		});

		app.MapMethods("/", [HttpMethods.Get, HttpMethods.Post], (HttpContext context) =>
			ServeBrowserEntryAsync(context, authentication, backends, front, updateStatus));

		// Ensure the workspace backend is running and return its connect URL + status (+ the updater's state,
		// which the runner status page renders — runner staleness and a rollback stay visible to the user).
		app.MapGet("/backend", async context => {
			var backend = backends.Ensure();
			string status = await backends.StatusAsync(backend).ConfigureAwait(false);
			await context.Response.WriteAsJsonAsync(
				new BackendStatus(front.WorkerPageUrl(HostOf(context), backend), backend.Token, status, backend.WorkspaceRoot, updateStatus()),
				RunnerJson.Default.BackendStatus,
				cancellationToken: context.RequestAborted).ConfigureAwait(false);
		});
	}

	private static async Task ServeBrowserEntryAsync(
		HttpContext context,
		RunnerAuthentication authentication,
		BackendManager backends,
		ITlsFront front,
		Func<UpdateStatus> updateStatus) {
		if (HttpMethods.IsPost(context.Request.Method)) {
			if (!await authentication.SignInAsync(context).ConfigureAwait(false)) {
				await RunnerConnectPage.WriteAsync(context, invalidToken: true).ConfigureAwait(false);
				return;
			}

			await RunnerStatusPage.WriteAcceptedAsync(context).ConfigureAwait(false);
			return;
		}

		if (context.Request.QueryString.HasValue) {
			NoStore(context);
			context.Response.Redirect("/");
			return;
		}

		if (!authentication.BrowserMatches(context)) {
			await RunnerConnectPage.WriteAsync(context, invalidToken: false).ConfigureAwait(false);
			return;
		}

		var backend = backends.Ensure();
		string status = await backends.StatusAsync(backend).ConfigureAwait(false);
		var update = updateStatus();
		if (status != "running") {
			await RunnerStatusPage.WriteWaitingAsync(
				context,
				backend.WorkspaceRoot,
				status,
				update).ConfigureAwait(false);
			return;
		}

		string workerUrl = front.WorkerPageUrl(HostOf(context), backend);
		if (!TryGetWorkerUri(context.Request.Host, workerUrl, out var workerUri)) {
			await RunnerStatusPage.WriteConfigurationErrorAsync(
				context,
				$"The configured worker URL is not clean or does not share the runner hostname '{context.Request.Host.Host}'.")
				.ConfigureAwait(false);
			return;
		}

		new PersistentTokenCookie("weavie", backend.Token).Establish(context);
		if (UpdateRequiresAttention(update)) {
			await RunnerStatusPage.WriteAttentionAsync(
				context,
				backend.WorkspaceRoot,
				workerUri.AbsoluteUri,
				update).ConfigureAwait(false);
			return;
		}

		NoStore(context);
		context.Response.Redirect(workerUri.AbsoluteUri);
	}

	internal static bool UpdateRequiresAttention(UpdateStatus update) =>
		update.RunnerBehind
		|| update.Phase is "error" or "failed" or "rolled-back";

	private static bool IsBrowserEntry(HttpContext context) =>
		context.Request.Path == "/"
		&& (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsPost(context.Request.Method));

	internal static bool TryGetWorkerUri(
		HostString requestHost,
		string workerUrl,
		[NotNullWhen(true)] out Uri? workerUri) {
		if (!Uri.TryCreate(workerUrl, UriKind.Absolute, out workerUri)
			|| workerUri.Scheme is not ("http" or "https")
			|| workerUri.Query.Length != 0
			|| workerUri.Fragment.Length != 0
			|| workerUri.UserInfo.Length != 0) {
			return false;
		}

		return string.Equals(
			workerUri.Host.Trim('[', ']'),
			requestHost.Host.Trim('[', ']'),
			StringComparison.OrdinalIgnoreCase);
	}

	private static string HostOf(HttpContext ctx) {
		// The host the client used to reach the runner, minus any port — the worker lives on its own port.
		string host = ctx.Request.Host.Host;
		return string.IsNullOrEmpty(host) ? "127.0.0.1" : host;
	}

	private static void NoStore(HttpContext context) {
		context.Response.Headers.CacheControl = "no-store";
		context.Response.Headers["Referrer-Policy"] = "no-referrer";
	}
}

internal sealed record BackendStatus(string Url, string Token, string Status, string Workspace, UpdateStatus Update);

/// <summary>Source-generated contracts for the runner's control-plane responses (web defaults: camelCase).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(BackendStatus))]
internal sealed partial class RunnerJson : JsonSerializerContext;
