using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Weavie.Hosting.Web;

/// <summary>A host-only browser credential with one shared persistence and comparison policy.</summary>
public sealed class PersistentTokenCookie {
	private readonly string _token;
	private readonly byte[] _tokenBytes;

	/// <summary>Creates a token-derived cookie under <paramref name="namePrefix"/>.</summary>
	public PersistentTokenCookie(string namePrefix, string token) {
		ArgumentException.ThrowIfNullOrEmpty(namePrefix);
		ArgumentException.ThrowIfNullOrEmpty(token);
		_token = token;
		_tokenBytes = Encoding.UTF8.GetBytes(token);
		string digest = Convert.ToHexString(SHA256.HashData(_tokenBytes)).ToLowerInvariant();
		Name = $"{namePrefix}-{digest[..16]}";
	}

	/// <summary>The token-derived cookie name.</summary>
	public string Name { get; }

	/// <summary>Returns whether the request carries this exact cookie credential.</summary>
	public bool Matches(HttpContext context) {
		ArgumentNullException.ThrowIfNull(context);
		return context.Request.Cookies.TryGetValue(Name, out string? token) && TokenMatches(token);
	}

	/// <summary>Compares a presented token with this credential.</summary>
	public bool TokenMatches(string presented) =>
		CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _tokenBytes);

	/// <summary>Establishes this credential for the request's host.</summary>
	public void Establish(HttpContext context) {
		ArgumentNullException.ThrowIfNull(context);
		context.Response.Cookies.Append(Name, _token, new CookieOptions {
			HttpOnly = true,
			IsEssential = true,
			MaxAge = TimeSpan.FromDays(365),
			Path = "/",
			SameSite = SameSiteMode.Strict,
			Secure = ExternalScheme(context) == "https",
		});
	}

	private static string ExternalScheme(HttpContext context) {
		string forwarded = context.Request.Headers["X-Forwarded-Proto"].ToString();
		return forwarded is "http" or "https" ? forwarded : context.Request.Scheme;
	}
}
