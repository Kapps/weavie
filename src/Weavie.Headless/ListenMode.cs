using System.Net;

namespace Weavie.Headless;

/// <summary>
/// The host's listening decision, resolved once from CLI/env. Remote listening is opt-in (<c>--remote</c>) and
/// the <see cref="Remote"/> case carries a non-null token, so auth keys off the mode, not token presence. Only
/// <see cref="Remote"/> can bind a network interface and it mandates the token, so an exposed-but-unauthenticated
/// host is unrepresentable. <see cref="Local"/> binds loopback and needs no auth.
/// </summary>
internal abstract record ListenMode {
	private ListenMode() {
	}

	/// <summary>Loopback only, no auth. The default for local dev / a host driven by a native shell.</summary>
	internal sealed record Local : ListenMode;

	/// <summary>A network bind with a required token; auth is enforced for every request.</summary>
	internal sealed record Remote(string Bind, string Token) : ListenMode;

	/// <summary>The interface to bind for this mode (loopback unless remote).</summary>
	public string BindAddress => this is Remote remote ? remote.Bind : "127.0.0.1";

	/// <summary>
	/// Resolves the mode, or <c>(null, error)</c> for an unsafe combination so the caller fails closed. Never
	/// yields remote without a token, and refuses a network bind unless <c>--remote</c> was given, so "exposed"
	/// always implies "authenticated."
	/// </summary>
	public static (ListenMode? Mode, string? Error) Resolve(string[] args) {
		bool remote = HasFlag(args, "--remote")
			|| IsTruthy(Environment.GetEnvironmentVariable("WEAVIE_SERVE_REMOTE"));
		string? bind = Value(args, "--bind") ?? Environment.GetEnvironmentVariable("WEAVIE_SERVE_BIND");
		string? token = Value(args, "--token") ?? Environment.GetEnvironmentVariable("WEAVIE_SERVE_TOKEN");
		token = string.IsNullOrEmpty(token) ? null : token;

		if (remote) {
			return token is null
				? (null, "remote listening (--remote) requires a token — pass --token <t> or set WEAVIE_SERVE_TOKEN.")
				: (new Remote(string.IsNullOrEmpty(bind) ? "0.0.0.0" : bind, token), null);
		}

		// Local mode: refuse anything that would expose the host or imply auth, so a network bind is only
		// reachable via --remote (which mandates a token).
		if (bind is not null && !IsLoopback(bind)) {
			return (null, $"--bind '{bind}' exposes a network interface and requires --remote (which mandates a token). Refusing.");
		}

		if (token is not null) {
			return (null, "--token only applies with --remote; refusing an ambiguous configuration.");
		}

		return (new Local(), null);
	}

	private static bool IsLoopback(string bind) =>
		bind is "127.0.0.1" or "::1" or "localhost"
		|| (IPAddress.TryParse(bind, out var ip) && IPAddress.IsLoopback(ip));

	private static bool HasFlag(string[] args, string name) => Array.IndexOf(args, name) >= 0;

	private static bool IsTruthy(string? value) => value is "1" or "true" or "TRUE" or "yes";

	private static string? Value(string[] args, string name) {
		for (int i = 0; i < args.Length - 1; i++) {
			if (args[i] == name) {
				return args[i + 1];
			}
		}

		return null;
	}
}
