using System.Security.Cryptography;

namespace Weavie.Runner;

/// <summary>
/// Resolved configuration for the runner daemon, read once at startup from CLI args, then environment
/// variables, then defaults. <see cref="RunnerToken"/> gates the control plane; <see cref="Bind"/> /
/// <see cref="Port"/> are where the control plane listens; <see cref="WorkspaceRoot"/> is the repository
/// whose worktrees back sessions; <see cref="HeadlessPath"/> is the Weavie.Headless build the workers run.
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

	/// <summary>Builds the options from process args + environment, generating a token when none is supplied.</summary>
	public static RunnerOptions Resolve(string[] args) {
		string? workspace = Arg(args, "--workspace") ?? Environment.GetEnvironmentVariable("WEAVIE_RUNNER_WORKSPACE");
		string root = string.IsNullOrEmpty(workspace) ? Environment.CurrentDirectory : Path.GetFullPath(workspace);

		string? headless = Arg(args, "--headless") ?? Environment.GetEnvironmentVariable("WEAVIE_HEADLESS_PATH");
		string headlessPath = string.IsNullOrEmpty(headless) ? ProbeHeadlessPath() : Path.GetFullPath(headless);

		string bind = Arg(args, "--bind") ?? Environment.GetEnvironmentVariable("WEAVIE_RUNNER_BIND") ?? "0.0.0.0";
		string workerBind = Arg(args, "--worker-bind") ?? Environment.GetEnvironmentVariable("WEAVIE_RUNNER_WORKER_BIND") ?? bind;
		int port = int.TryParse(Arg(args, "--port") ?? Environment.GetEnvironmentVariable("WEAVIE_RUNNER_PORT"), out int p) ? p : 8800;

		string? token = Arg(args, "--token") ?? Environment.GetEnvironmentVariable("WEAVIE_RUNNER_TOKEN");
		string runnerToken = string.IsNullOrEmpty(token) ? NewToken() : token;

		return new RunnerOptions {
			WorkspaceRoot = root,
			HeadlessPath = headlessPath,
			Bind = bind,
			WorkerBind = workerBind,
			Port = port,
			RunnerToken = runnerToken,
		};
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
	/// Best-effort default for the worker binary: the sibling Weavie.Headless build output, derived from the
	/// runner's own base directory by swapping the project name in the path (works for a standard dev build).
	/// Override with <c>--headless</c> / <c>WEAVIE_HEADLESS_PATH</c> when that convention doesn't hold.
	/// </summary>
	private static string ProbeHeadlessPath() {
		string baseDir = AppContext.BaseDirectory; // …/src/Weavie.Runner/bin/<cfg>/net10.0/
		string candidate = baseDir
			.Replace($"{Path.DirectorySeparatorChar}Weavie.Runner{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}Weavie.Headless{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
		string dll = Path.Combine(candidate, "Weavie.Headless.dll");
		return File.Exists(dll) ? dll : Path.Combine(baseDir, "Weavie.Headless.dll");
	}
}
