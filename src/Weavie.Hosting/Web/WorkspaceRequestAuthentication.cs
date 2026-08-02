using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Weavie.Hosting.Web;

/// <summary>Authenticates same-origin browser requests with a persistent cookie and remote transports with a token.</summary>
internal sealed class WorkspaceRequestAuthentication {
	private readonly string _token;
	private readonly byte[] _tokenBytes;

	public WorkspaceRequestAuthentication(string token) {
		ArgumentException.ThrowIfNullOrEmpty(token);
		_token = token;
		_tokenBytes = Encoding.UTF8.GetBytes(token);
		string digest = Convert.ToHexString(SHA256.HashData(_tokenBytes)).ToLowerInvariant();
		CookieName = $"weavie-{digest[..16]}";
	}

	public string CookieName { get; }

	public bool Authenticates(HttpContext context) =>
		CookieMatches(context) || TransportTokenMatches(context);

	public bool CookieMatches(HttpContext context) =>
		context.Request.Cookies.TryGetValue(CookieName, out string? token) && TokenMatches(token);

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

	public void EstablishCookie(HttpContext context) {
		context.Response.Cookies.Append(CookieName, _token, new CookieOptions {
			HttpOnly = true,
			IsEssential = true,
			MaxAge = TimeSpan.FromDays(365),
			Path = "/",
			SameSite = SameSiteMode.Strict,
			Secure = ExternalScheme(context) == "https",
		});
	}

	public bool CookieWebSocketOriginMatches(HttpContext context) {
		string origin = context.Request.Headers.Origin.ToString();
		return origin.Length > 0
			&& string.Equals(
				origin,
				$"{ExternalScheme(context)}://{context.Request.Host}",
				StringComparison.OrdinalIgnoreCase);
	}

	private bool TokenMatches(string presented) =>
		CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _tokenBytes);

	private static string ExternalScheme(HttpContext context) {
		string forwarded = context.Request.Headers["X-Forwarded-Proto"].ToString();
		return forwarded is "http" or "https" ? forwarded : context.Request.Scheme;
	}
}
