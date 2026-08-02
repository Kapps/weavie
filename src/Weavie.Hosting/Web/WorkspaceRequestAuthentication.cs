using Microsoft.AspNetCore.Http;

namespace Weavie.Hosting.Web;

/// <summary>Authenticates same-origin browser requests with a persistent cookie and remote transports with a token.</summary>
internal sealed class WorkspaceRequestAuthentication {
	private readonly PersistentTokenCookie _cookie;

	public WorkspaceRequestAuthentication(string token) {
		_cookie = new PersistentTokenCookie("weavie", token);
	}

	public string CookieName => _cookie.Name;

	public bool Authenticates(HttpContext context) =>
		CookieMatches(context) || TransportTokenMatches(context);

	public bool CookieMatches(HttpContext context) =>
		_cookie.Matches(context);

	public bool TransportTokenMatches(HttpContext context) =>
		context.Request.Query.TryGetValue("token", out var token) && TokenMatches(token.ToString());

	public bool BootstrapTokenMatches(HttpContext context) =>
		context.Request.Query.TryGetValue("bootstrap", out var token) && TokenMatches(token.ToString());

	public async Task SignInAsync(HttpContext context) {
		if (!context.Request.HasFormContentType) {
			await WorkspaceConnectPage.WriteAsync(context, invalidToken: true).ConfigureAwait(false);
			return;
		}

		var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
		if (!TokenMatches(form["token"].ToString())) {
			await WorkspaceConnectPage.WriteAsync(context, invalidToken: true).ConfigureAwait(false);
			return;
		}

		EstablishCookie(context);
		context.Response.Redirect("/index.html");
	}

	public void EstablishCookie(HttpContext context) => _cookie.Establish(context);

	public bool CookieWebSocketOriginMatches(HttpContext context) {
		string origin = context.Request.Headers.Origin.ToString();
		return origin.Length > 0
			&& string.Equals(
				origin,
				$"{ExternalScheme(context)}://{context.Request.Host}",
				StringComparison.OrdinalIgnoreCase);
	}

	private bool TokenMatches(string presented) =>
		_cookie.TokenMatches(presented);

	private static string ExternalScheme(HttpContext context) {
		string forwarded = context.Request.Headers["X-Forwarded-Proto"].ToString();
		return forwarded is "http" or "https" ? forwarded : context.Request.Scheme;
	}
}
