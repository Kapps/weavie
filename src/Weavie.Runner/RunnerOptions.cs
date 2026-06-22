using System.Security.Cryptography;

namespace Weavie.Runner;

/// <summary>
/// Resolved configuration for the runner daemon, read once at startup from CLI args, then environment, then
/// defaults.
/// </summary>
public sealed record RunnerOptions {
	/// <summary>The repository root whose worktrees back sessions (the box's checkout).</summary>
	public required string WorkspaceRoot { get; init; }

	/// <summary>Path to the Weavie.Headless executable or <c>.dll</c> the workers run.</summary>
	public required string HeadlessPath { get; init; }

	/// <summary>Interface the control plane binds (e.g. <c>0.0.0.0</c> to accept remote clients).</summary>
	public string Bind { get; init; } = "0.0.0.0";

	/// <summary>The control-plane port.</summary>
	public int Port { get; init; } = 8800;

	/// <summary>The interface the spawned worker headless processes bind (defaults to <see cref="Bind"/>).</summary>
	public string WorkerBind { get; init; } = "0.0.0.0";

	/// <summary>The bearer token a client must present to drive the control plane.</summary>
	public required string RunnerToken { get; init; }

	/// <summary>
	/// Builds the options from args + environment, generating a token when none is supplied. Returns
	/// <c>(null, error)</c> when the Weavie.Headless worker binary can't be located, so a missing worker fails
	/// loudly at startup instead of crash-looping the supervisor on a dead path.
	/// </summary>
	public static (RunnerOptions? Options, string? Error) Resolve(string[] args) {
		string? workspace = Arg(args, "--workspace") ?? Environment.GetEnvironmentVariable("WEAVIE_RUNNER_WORKSPACE");
		string root = string.IsNullOrEmpty(workspace) ? Environment.CurrentDirectory : Path.GetFullPath(workspace);

		var (headlessPath, headlessError) = ResolveHeadlessPath(args);
		if (headlessPath is null) {
			return (null, headlessError);
		}

		string bind = Arg(args, "--bind") ?? Environment.GetEnvironmentVariable("WEAVIE_RUNNER_BIND") ?? "0.0.0.0";
		string workerBind = Arg(args, "--worker-bind") ?? Environment.GetEnvironmentVariable("WEAVIE_RUNNER_WORKER_BIND") ?? bind;
		int port = int.TryParse(Arg(args, "--port") ?? Environment.GetEnvironmentVariable("WEAVIE_RUNNER_PORT"), out int p) ? p : 8800;

		string? token = Arg(args, "--token") ?? Environment.GetEnvironmentVariable("WEAVIE_RUNNER_TOKEN");
		string runnerToken = string.IsNullOrEmpty(token) ? NewToken() : token;

		return (new RunnerOptions {
			WorkspaceRoot = root,
			HeadlessPath = headlessPath,
			Bind = bind,
			WorkerBind = workerBind,
			Port = port,
			RunnerToken = runnerToken,
		}, null);
	}

	/// <summary>A URL-safe random token (128 bits of entropy, lowercase hex).</summary>
	public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

	private static string? Arg(string[] args, string name) {
		for (int i = 0; i < args.Length - 1; i++) {
			if (args[i] == name) {
				return args[i + 1];
			}
		}

		return null;
	}

	/// <summary>
	/// Resolves the worker binary, or <c>(null, error)</c> when it can't be found — never an unconfirmed path. An
	/// explicit <c>--headless</c> / <c>WEAVIE_HEADLESS_PATH</c> must point at a real file; otherwise it probes
	/// the known build locations and, on failure, errors listing every path it checked.
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
	/// The build locations to probe for the worker dll, nearest-first: the sibling Weavie.Headless project output
	/// (dev <c>dotnet run</c>), the <c>worker/</c> subfolder that <c>dotnet publish Weavie.Runner</c> stages next
	/// to the runner (the deployed layout — see Weavie.Runner.csproj), then a manual same-dir co-locate. Only the
	/// last <c>Weavie.Runner</c> path segment is rewritten, so a checkout under a <c>Weavie.Runner</c> directory
	/// can't be corrupted.
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
