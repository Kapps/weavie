using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Weavie.Core.FileSystem;
using Weavie.Core.Git;
using Weavie.Core.Remote;
using Weavie.Core.Workspaces;

namespace Weavie.WorktreeServe;

internal sealed class WorktreeServeApp(ITailscaleCli tailscale) {
	private const string StateMarkerContents = "weavie-worktree-serve-state-v1\n";
	private const string StateMarkerName = ".weavie-worktree-serve-state";
	private readonly ITailscaleCli _tailscale = tailscale;

	public async Task RunAsync(WorktreeServeOptions options, CancellationToken cancellationToken) {
		ArgumentNullException.ThrowIfNull(options);
		string invocationDirectory = Environment.CurrentDirectory;
		string sourceRoot = await TransientCommand.CaptureAsync(
			"git", ["rev-parse", "--show-toplevel"], invocationDirectory, cancellationToken).ConfigureAwait(false);
		string selectedWorktree = ResolveDirectory(options.Workspace ?? sourceRoot, invocationDirectory, "workspace");
		var worktrees = await new GitService().ListWorktreesAsync(selectedWorktree, cancellationToken).ConfigureAwait(false);
		string workspace = ResolveDirectory(PrimaryWorktree(worktrees).Path, invocationDirectory, "primary workspace");
		string? explicitStateRoot = options.StateRoot is null
			? null
			: Path.GetFullPath(options.StateRoot, invocationDirectory);
		string stateRoot = explicitStateRoot ?? DefaultStateRoot(sourceRoot);
		string productionRoot = ProductionStateRoot();
		RejectStateOverlap(stateRoot, [productionRoot, sourceRoot, workspace, selectedWorktree]);
		string runRoot = Directory.CreateTempSubdirectory("weavie-worktree-serve-").FullName;

		Exception? failure = null;
		TailscaleServeSession? serve = null;
		SupervisedProcess? headless = null;
		PortLease? lease = null;
		PortLease? stateLease = null;
		bool serveStarted = false;
		try {
			ClaimStateRoot(stateRoot);
			stateLease = PortLease.AcquireState(stateRoot);
			lease = PortLease.Acquire(options.HttpsPort);
			string magicDns = TailscaleServeSession.DiscoverMagicDns(_tailscale);
			EnsurePortAvailable(options.HttpsPort);

			Console.WriteLine($"[worktree-serve] source:    {sourceRoot}");
			Console.WriteLine($"[worktree-serve] workspace: {workspace}");
			Console.WriteLine($"[worktree-serve] state:     {stateRoot}");
			await BuildAsync(sourceRoot, runRoot, cancellationToken).ConfigureAwait(false);
			EnsurePortAvailable(options.HttpsPort);
			var preview = PreviewStateBootstrap.Refresh(productionRoot, stateRoot, workspace, selectedWorktree);
			Console.WriteLine($"[worktree-serve] session:   {preview.SelectedSession.Value} ({preview.SelectedProvider})");

			var readiness = new HeadlessReadiness();
			headless = CreateHeadless(runRoot, workspace, stateRoot, readiness);
			headless.Start();
			var endpoint = await AwaitHeadlessAsync(headless, readiness, cancellationToken).ConfigureAwait(false);

			serve = new TailscaleServeSession(_tailscale, magicDns, options.HttpsPort, endpoint.Target);
			serveStarted = true;
			await serve.StartAsync(cancellationToken).ConfigureAwait(false);
			string url = endpoint.BrowserUrl(magicDns, options.HttpsPort);
			Console.WriteLine();
			Console.WriteLine("[worktree-serve] Ctrl+click to connect:");
			Console.WriteLine(url);
			Console.WriteLine("[worktree-serve] Press Ctrl+C to stop the preview.");
			Console.Out.Flush();

			await WaitForLifetimeAsync(headless, serve, cancellationToken).ConfigureAwait(false);
		} catch (Exception ex) {
			failure = ex;
		}

		if (serveStarted && serve is not null) {
			try {
				await serve.StopAndVerifyAsync().ConfigureAwait(false);
			} catch (Exception ex) {
				failure = failure is null ? ex : new AggregateException(failure, ex);
			}
		}
		serve?.Dispose();
		if (headless is not null) {
			headless.Stop();
			try {
				await headless.Completion.ConfigureAwait(false);
			} catch (Exception ex) {
				failure = failure is null ? ex : new AggregateException(failure, ex);
			}
			headless.Dispose();
		}
		lease?.Dispose();
		stateLease?.Dispose();
		try {
			Directory.Delete(runRoot, recursive: true);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			failure = failure is null ? ex : new AggregateException(failure, ex);
		}

		if (failure is not null) {
			ExceptionDispatchInfo.Capture(failure).Throw();
		}
	}

	private static async Task BuildAsync(string sourceRoot, string runRoot, CancellationToken cancellationToken) {
		string webRoot = Path.Combine(sourceRoot, "src", "web");
		var node = await NodeToolchain.EnsureAsync(sourceRoot, runRoot, cancellationToken).ConfigureAwait(false);
		var environment = node.ProcessEnvironment();
		await TransientCommand.RunAsync(
			node.Node,
			[node.CorepackScript, "pnpm", "install", "--frozen-lockfile", "--reporter=append-only"],
			webRoot,
			environment,
			cancellationToken).ConfigureAwait(false);
		string headlessProject = Path.Combine(sourceRoot, "src", "Weavie.Headless", "Weavie.Headless.csproj");
		string publishRoot = Path.Combine(runRoot, "publish");
		await TransientCommand.RunAsync(
			"dotnet",
			[
				"publish", headlessProject, "-c", "Release", "--no-self-contained", "-o", publishRoot, "--nologo", "--tl:off",
			],
			sourceRoot,
			environment,
			cancellationToken).ConfigureAwait(false);
	}

	private static SupervisedProcess CreateHeadless(
		string runRoot,
		string workspace,
		string stateRoot,
		HeadlessReadiness readiness) {
		string assembly = Path.Combine(runRoot, "publish", "Weavie.Headless.dll");
		var info = new ProcessStartInfo("dotnet") {
			WorkingDirectory = workspace,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		foreach (string arg in new[] { assembly, "--port", "0", "--workspace", workspace }) {
			info.ArgumentList.Add(arg);
		}
		info.Environment["WEAVIE_ROOT"] = stateRoot;
		return new SupervisedProcess(
			"headless",
			info,
			line => {
				readiness.Accept(line);
				if (!HeadlessReadiness.IsTokenLine(line)) {
					Console.WriteLine(line);
				}
			},
			line => Console.Error.WriteLine(line));
	}

	private static async Task<HeadlessEndpoint> AwaitHeadlessAsync(
		SupervisedProcess headless,
		HeadlessReadiness readiness,
		CancellationToken cancellationToken) {
		var cancelled = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
		var completed = await Task.WhenAny(readiness.Ready, headless.Completion, cancelled).ConfigureAwait(false);
		cancellationToken.ThrowIfCancellationRequested();
		if (completed == readiness.Ready) {
			return await readiness.Ready.ConfigureAwait(false);
		}
		if (completed == headless.Completion) {
			throw new InvalidOperationException(
				$"Weavie.Headless exited before publishing its loopback endpoint (code {await headless.Completion.ConfigureAwait(false)}).");
		}

		throw new UnreachableException();
	}

	private static async Task WaitForLifetimeAsync(
		SupervisedProcess headless,
		TailscaleServeSession serve,
		CancellationToken cancellationToken) {
		var cancelled = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
		var completed = await Task.WhenAny(headless.Completion, serve.Completion, cancelled).ConfigureAwait(false);
		cancellationToken.ThrowIfCancellationRequested();

		string name = completed == headless.Completion ? "Weavie.Headless" : "foreground tailscale serve";
		int exitCode = await ((Task<int>)completed).ConfigureAwait(false);
		throw new InvalidOperationException($"{name} exited unexpectedly (code {exitCode}).");
	}

	private void EnsurePortAvailable(int port) {
		if (TailscaleServeSession.ReadStatus(_tailscale).PortIsOccupied(port)) {
			throw new InvalidOperationException(
				$"Tailscale HTTPS port {port} is already configured; the existing route was not changed.");
		}
	}

	private static string ResolveDirectory(string path, string relativeTo, string label) {
		string fullPath = Path.GetFullPath(path, relativeTo);
		return Directory.Exists(fullPath)
			? fullPath
			: throw new DirectoryNotFoundException($"{label} directory does not exist: {fullPath}");
	}

	internal static void RejectProductionState(string stateRoot) =>
		RejectProductionState(stateRoot, ProductionStateRoot());

	internal static void RejectProductionState(string stateRoot, string productionStateRoot) =>
		RejectStateOverlap(stateRoot, [productionStateRoot]);

	internal static void RejectStateOverlap(string stateRoot, IReadOnlyList<string> protectedRoots) {
		foreach (string protectedRoot in protectedRoots) {
			if (PhysicalPath.IsSameOrDescendant(stateRoot, protectedRoot)
				|| PhysicalPath.IsSameOrDescendant(protectedRoot, stateRoot)) {
				throw new InvalidOperationException(
					$"the preview state root overlaps protected path '{protectedRoot}'.");
			}
		}
	}

	internal static void ClaimStateRoot(string stateRoot) {
		string root = Path.GetFullPath(stateRoot);
		var rootInfo = new DirectoryInfo(root);
		if (rootInfo.LinkTarget is not null) {
			throw new InvalidOperationException("the preview state root may not be a filesystem link.");
		}
		Directory.CreateDirectory(root);
		string marker = Path.Combine(root, StateMarkerName);
		if (File.Exists(marker)) {
			ValidateStateMarker(marker);
			return;
		}
		if (Directory.EnumerateFileSystemEntries(root).Any()) {
			throw new InvalidOperationException(
				"the preview state root is not empty and has no Weavie worktree-preview ownership marker.");
		}
		string pending = $"{marker}.{Guid.NewGuid():N}.pending";
		try {
			File.WriteAllText(pending, StateMarkerContents);
			File.Move(pending, marker);
		} catch (IOException) when (File.Exists(marker)) {
			ValidateStateMarker(marker);
		} finally {
			File.Delete(pending);
		}
	}

	private static void ValidateStateMarker(string marker) {
		if (new FileInfo(marker).LinkTarget is not null
			|| !string.Equals(File.ReadAllText(marker), StateMarkerContents, StringComparison.Ordinal)) {
			throw new InvalidOperationException("the preview state root has an invalid ownership marker.");
		}
	}

	internal static string DefaultStateRoot(string sourceRoot) =>
		Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			".weavie-previews",
			"worktree-serve",
			WorkspaceId.ForPath(sourceRoot).Value);

	internal static GitWorktree PrimaryWorktree(IReadOnlyList<GitWorktree> worktrees) =>
		worktrees.FirstOrDefault(worktree => !worktree.IsBare)
		?? throw new InvalidOperationException("git did not report a primary non-bare worktree.");

	private static string ProductionStateRoot() =>
		Environment.GetEnvironmentVariable("WEAVIE_ROOT") is { Length: > 0 } configured
			? Path.GetFullPath(configured)
			: Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weavie"));
}
