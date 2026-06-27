namespace Weavie.Core.Review;

/// <summary>
/// A forge repository identity — <c>host</c> picks the provider implementation (e.g. <c>github.com</c>),
/// <c>owner</c>/<c>name</c> address the repo. Derived from a git remote URL, so the user never types it.
/// </summary>
public sealed record RepoRef(string Host, string Owner, string Name) {
	/// <summary>
	/// Parses a git remote URL into a <see cref="RepoRef"/>, normalizing the forms git emits — HTTPS
	/// (<c>https://github.com/owner/repo.git</c>), SCP-like (<c>git@github.com:owner/repo.git</c>), and SSH
	/// (<c>ssh://git@github.com/owner/repo.git</c>) — by taking the host plus the last two non-empty path
	/// segments (stripping a trailing <c>.git</c>). Returns <c>null</c> when the URL has no host or fewer than
	/// two path segments.
	/// </summary>
	public static RepoRef? FromRemoteUrl(string? url) {
		if (string.IsNullOrWhiteSpace(url)) {
			return null;
		}

		string text = url.Trim();
		string host;
		string path;

		// SCP-like "git@host:owner/repo.git" — no scheme, host and path split on the first colon.
		int scheme = text.IndexOf("://", StringComparison.Ordinal);
		if (scheme < 0 && text.Contains('@', StringComparison.Ordinal) && text.Contains(':', StringComparison.Ordinal)) {
			int at = text.IndexOf('@', StringComparison.Ordinal);
			int colon = text.IndexOf(':', at);
			host = text[(at + 1)..colon];
			path = text[(colon + 1)..];
		} else {
			string afterScheme = scheme < 0 ? text : text[(scheme + 3)..];
			int slash = afterScheme.IndexOf('/', StringComparison.Ordinal);
			if (slash < 0) {
				return null;
			}

			string authority = afterScheme[..slash];
			path = afterScheme[(slash + 1)..];
			// Drop any userinfo ("git@") from the authority, then any port.
			int at = authority.IndexOf('@', StringComparison.Ordinal);
			host = at >= 0 ? authority[(at + 1)..] : authority;
			int port = host.IndexOf(':', StringComparison.Ordinal);
			if (port >= 0) {
				host = host[..port];
			}
		}

		if (host.Length == 0) {
			return null;
		}

		string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (segments.Length < 2) {
			return null;
		}

		string owner = segments[^2];
		string name = segments[^1];
		if (name.EndsWith(".git", StringComparison.Ordinal)) {
			name = name[..^4];
		}

		return owner.Length == 0 || name.Length == 0 ? null : new RepoRef(host, owner, name);
	}
}
