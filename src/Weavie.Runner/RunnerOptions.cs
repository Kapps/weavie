using System.Net;
using System.Security.Cryptography;

namespace Weavie.Runner;

/// <summary>How the runner exposes its endpoints over TLS. See docs/specs/tls-on-the-runner.md.</summary>
public enum TlsMode {
	/// <summary>Loopback <c>http</c> only (local / headless use); a network bind is refused (fail closed).</summary>
	None,

	/// <summary>
	/// <c>https</c> URLs pointing at an operator-run TLS terminator (Caddy / nginx / a cloud LB) in front of the
	/// loopback ports. Requires <c>--public-host</c>.
	/// </summary>
	Proxy,

	/// <summary>Turnkey: the runner runs <c>tailscale serve</c> to front the loopback ports with the node's trusted cert.</summary>
	Tailscale,
}

/// <summary>
/// Resolved configuration for the runner daemon (CLI args, then environment, then defaults).
/// </summary>
public sealed record RunnerOptions {
	/// <summary>The repository root whose worktrees back sessions (the box's checkout).</summary>
	public required string WorkspaceRoot { get; init; }

	/// <summary>Path to the Weavie.Headless executable or <c>.dll</c> the workers run.</summary>
	public required string HeadlessPath { get; init; }

	/// <summary>Interface the control plane binds. Secured modes front loopback, so this stays loopback there.</summary>
	public string Bind { get; init; } = "127.0.0.1";

	/// <summary>The control-plane port (the loopback port a TLS front maps in secured modes).</summary>
	public int Port { get; init; } = 8800;

	/// <summary>The interface the spawned worker headless processes bind (loopback; secured modes front it).</summary>
	public string WorkerBind { get; init; } = "127.0.0.1";

	/// <summary>How the endpoints are exposed over TLS.</summary>
	public TlsMode Tls { get; init; } = TlsMode.None;

	/// <summary>The public hostname a TLS terminator serves (required for <see cref="TlsMode.Proxy"/>).</summary>
	public string? PublicHost { get; init; }

	/// <summary>
	/// The worker's loopback port. <c>null</c> allocates a free port per worker (local use); secured modes pin a
	/// fixed port so the TLS-front mapping survives worker restarts.
	/// </summary>
	public int? WorkerPort { get; init; }

	/// <summary>The public https port the worker is fronted on (embedded in the advertised <c>wss://</c> URL).</summary>
	public int WorkerHttpsPort { get; init; } = 8443;

	/// <summary>The public https port the control plane is fronted on.</summary>
	public int ControlHttpsPort { get; init; } = 443;

	/// <summary>The bearer token a client must present to drive the control plane.</summary>
	public required string RunnerToken { get; init; }

	/// <summary>
	/// Whether the runner polls green-main release bundles and drain-swaps the worker onto them
	/// (<c>--auto-update</c>). Off by default; the runner itself only changes version on restart either way.
	/// </summary>
	public bool AutoUpdate { get; init; }

	/// <summary>GitHub token for the update poll/download (<c>--github-token</c>); public repos work anonymously.</summary>
	public string? GitHubToken { get; init; }

	/// <summary>
	/// Builds the options from args + environment, generating a token when none is supplied. Returns
	/// <c>(null, error)</c> when the worker binary can't be located or an unsafe combination is requested, so the
	/// runner fails loudly at startup instead of exposing an unencrypted endpoint or crash-looping a dead path.
	/// </summary>
	public static (RunnerOptions? Options, string? Error) Resolve(string[] args) {
		string? workspace = Arg(args, "--workspace") ?? Environment.GetEnvironmentVariable("WEAVIE_RUNNER_WORKSPACE");
		string root = string.IsNullOrEmpty(workspace) ? Environment.CurrentDirectory : Path.GetFullPath(workspace);

		var (headlessPath, headlessError) = ResolveHeadlessPath(args);
		if (headlessPath is null) {
			return (null, headlessError);
		}

		var (tls, tlsError) = ResolveTls(args);
		if (tlsError is not null) {
			return (null, tlsError);
		}

		string bind = Arg(args, "--bind") ?? Environment.GetEnvironmentVariable("WEAVIE_RUNNER_BIND") ?? "127.0.0.1";
		string workerBind = Arg(args, "--worker-bind") ?? Environment.GetEnvironmentVariable("WEAVIE_RUNNER_WORKER_BIND") ?? bind;
		int port = ParseInt(Arg(args, "--port") ?? Environment.GetEnvironmentVariable("WEAVIE_RUNNER_PORT"), 8800);
		string? publicHost = Arg(args, "--public-host") ?? Environment.GetEnvironmentVariable("WEAVIE_RUNNER_PUBLIC_HOST");
		publicHost = string.IsNullOrEmpty(publicHost) ? null : publicHost;

		// A typo'd port must fail loudly, not silently fall back to the default the operator never sees.
		var (workerHttpsValue, workerHttpsError) = ParsePort(args, "--worker-https-port");
		var (controlHttpsValue, controlHttpsError) = ParsePort(args, "--control-https-port");
		var (workerPortValue, workerPortError) = ParsePort(args, "--worker-port");
		if ((workerHttpsError ?? controlHttpsError ?? workerPortError) is { } portError) {
			return (null, portError);
		}

		int workerHttps = workerHttpsValue ?? 8443;
		int controlHttps = controlHttpsValue ?? 443;
		int? workerPort = workerPortValue;

		// Fail closed: an exposed endpoint must terminate TLS. Secured modes front loopback themselves, so a
		// stable worker port is pinned (the front mapping must survive worker restarts).
		if (tls == TlsMode.None) {
			if (!IsLoopback(bind)) {
				return (null, $"--bind '{bind}' exposes the runner without TLS. Use --tls tailscale (turnkey) or --tls proxy, or bind loopback. Refusing.");
			}

			if (!IsLoopback(workerBind)) {
				return (null, $"--worker-bind '{workerBind}' exposes the worker without TLS. Use --tls tailscale or --tls proxy, or bind loopback. Refusing.");
			}
		} else {
			workerPort ??= 8701;
		}

		if (tls == TlsMode.Proxy && publicHost is null) {
			return (null, "--tls proxy requires --public-host <host> (the hostname your TLS terminator serves) so /backend advertises the right wss:// URL.");
		}

		string? token = Arg(args, "--token") ?? Environment.GetEnvironmentVariable("WEAVIE_RUNNER_TOKEN");
		string runnerToken = string.IsNullOrEmpty(token) ? NewToken() : token;

		bool autoUpdate = Flag(args, "--auto-update", "WEAVIE_RUNNER_AUTO_UPDATE");
		string? gitHubToken = Arg(args, "--github-token") ?? Environment.GetEnvironmentVariable("WEAVIE_RUNNER_GITHUB_TOKEN");
		gitHubToken = string.IsNullOrEmpty(gitHubToken) ? null : gitHubToken;

		return (new RunnerOptions {
			WorkspaceRoot = root,
			HeadlessPath = headlessPath,
			Bind = bind,
			WorkerBind = workerBind,
			Port = port,
			Tls = tls,
			PublicHost = publicHost,
			WorkerPort = workerPort,
			WorkerHttpsPort = workerHttps,
			ControlHttpsPort = controlHttps,
			RunnerToken = runnerToken,
			AutoUpdate = autoUpdate,
			GitHubToken = gitHubToken,
		}, null);
	}

	// Every flag Resolve reads. An arg outside this set is a typo we warn about (not reject) — the runner
	// still starts, but the operator sees that e.g. --autoupdate never enabled --auto-update.
	private static readonly HashSet<string> KnownFlags = new(StringComparer.Ordinal) {
		"--workspace", "--headless", "--tls", "--bind", "--worker-bind", "--port", "--public-host",
		"--worker-https-port", "--control-https-port", "--worker-port", "--token", "--auto-update", "--github-token",
	};

	/// <summary>The <c>--</c>-prefixed args <see cref="Resolve"/> doesn't recognize, in argv order (for a startup warning).</summary>
	public static IReadOnlyList<string> UnknownArgs(string[] args) {
		ArgumentNullException.ThrowIfNull(args);
		return [.. args.Where(a => a.StartsWith("--", StringComparison.Ordinal) && !KnownFlags.Contains(a))];
	}

	/// <summary>A URL-safe random token (128 bits of entropy, lowercase hex).</summary>
	public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

	private static (TlsMode Mode, string? Error) ResolveTls(string[] args) {
		string? value = Arg(args, "--tls") ?? Environment.GetEnvironmentVariable("WEAVIE_RUNNER_TLS");
		if (string.IsNullOrEmpty(value)) {
			return (TlsMode.None, null);
		}

		return value.ToLowerInvariant() switch {
			"none" => (TlsMode.None, null),
			"proxy" => (TlsMode.Proxy, null),
			"tailscale" => (TlsMode.Tailscale, null),
			_ => (TlsMode.None, $"--tls '{value}' is not a known mode (use none, proxy, or tailscale)."),
		};
	}

	private static bool IsLoopback(string bind) =>
		bind is "127.0.0.1" or "::1" or "localhost"
		|| (IPAddress.TryParse(bind, out var ip) && IPAddress.IsLoopback(ip));

	private static int ParseInt(string? value, int fallback) => int.TryParse(value, out int parsed) ? parsed : fallback;

	// An explicitly-provided numeric option: absent → (null, null); valid → (value, null); present-but-unparseable
	// → (null, error), so a typo fails loudly instead of silently using the default.
	private static (int? Value, string? Error) ParsePort(string[] args, string name) {
		string? raw = Arg(args, name);
		if (raw is null) {
			return (null, null);
		}

		return int.TryParse(raw, out int parsed) ? (parsed, null) : (null, $"{name} '{raw}' is not a valid port number.");
	}

	// A value-less switch: present in argv, or its env var set to 1/true.
	private static bool Flag(string[] args, string name, string envVar) =>
		args.Contains(name, StringComparer.Ordinal)
		|| Environment.GetEnvironmentVariable(envVar) is "1" or "true";

	private static string? Arg(string[] args, string name) {
		for (int i = 0; i < args.Length - 1; i++) {
			if (args[i] == name) {
				return args[i + 1];
			}
		}

		return null;
	}

	/// <summary>
	/// Resolves the worker binary, or <c>(null, error)</c> when it can't be found — never an unconfirmed path.
	/// An explicit <c>--headless</c> / <c>WEAVIE_HEADLESS_PATH</c> must point at a real file; otherwise probes
	/// the known build locations, erroring with every path checked on failure.
	/// </summary>
	private static (string? Path, string? Error) ResolveHeadlessPath(string[] args) {
		string? explicitPath = Arg(args, "--headless") ?? Environment.GetEnvironmentVariable("WEAVIE_HEADLESS_PATH");
		if (!string.IsNullOrEmpty(explicitPath)) {
			string full = Path.GetFullPath(explicitPath);
			return File.Exists(full)
				? (full, null)
				: (null, $"--headless '{explicitPath}' does not exist (resolved to '{full}').");
		}

		var candidates = ProbeCandidates();
		foreach (string candidate in candidates) {
			if (File.Exists(candidate)) {
				return (candidate, null);
			}
		}

		return (null,
			"could not locate the Weavie.Headless worker binary. Checked:\n  " + string.Join("\n  ", candidates)
			+ "\nBuild Weavie.Headless in the same configuration as the runner, or pass --headless <path-to-Weavie.Headless.dll>.");
	}

	/// <summary>
	/// Build locations to probe for the worker dll, nearest-first: dev sibling output, the published
	/// <c>worker/</c> subfolder, then a same-dir co-locate. Only the last <c>Weavie.Runner</c> path segment is
	/// rewritten, so a checkout under a <c>Weavie.Runner</c> directory can't be corrupted.
	/// </summary>
	private static IReadOnlyList<string> ProbeCandidates() {
		string baseDir = AppContext.BaseDirectory; // …/src/Weavie.Runner/bin/<cfg>/<tfm>/ (dev) or the deploy dir
		string sibling = ReplaceLast(
			baseDir,
			$"{Path.DirectorySeparatorChar}Weavie.Runner{Path.DirectorySeparatorChar}",
			$"{Path.DirectorySeparatorChar}Weavie.Headless{Path.DirectorySeparatorChar}");

		string[] candidates = [
			Path.Combine(sibling, "Weavie.Headless.dll"),
			Path.Combine(baseDir, "worker", "Weavie.Headless.dll"),
			Path.Combine(baseDir, "Weavie.Headless.dll"),
		];
		return candidates.Distinct(StringComparer.Ordinal).ToList();
	}

	/// <summary>Replaces the LAST occurrence of <paramref name="find"/> only (or returns the string unchanged).</summary>
	private static string ReplaceLast(string value, string find, string replacement) {
		int index = value.LastIndexOf(find, StringComparison.Ordinal);
		return index < 0
			? value
			: string.Concat(value.AsSpan(0, index), replacement, value.AsSpan(index + find.Length));
	}
}
