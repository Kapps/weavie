using Microsoft.AspNetCore.Http;
using Weavie.Hosting.Web;

namespace Weavie.Runner;

/// <summary>Separates the runner's browser credential from its explicit machine credential.</summary>
internal sealed class RunnerAuthentication {
	private readonly PersistentTokenCookie _cookie;

	public RunnerAuthentication(string token) {
		_cookie = new PersistentTokenCookie("weavie-runner", token);
	}

	public bool BrowserMatches(HttpContext context) => _cookie.Matches(context);

	public bool BearerMatches(HttpContext context) {
		string header = context.Request.Headers.Authorization.ToString();
		return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
			&& _cookie.TokenMatches(header["Bearer ".Length..].Trim());
	}

	public async Task<bool> SignInAsync(HttpContext context) {
		if (!context.Request.HasFormContentType) {
			return false;
		}

		var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
		if (!_cookie.TokenMatches(form["token"].ToString())) {
			return false;
		}

		_cookie.Establish(context);
		return true;
	}
}
