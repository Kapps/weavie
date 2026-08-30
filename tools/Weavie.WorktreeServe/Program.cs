using System.Runtime.InteropServices;
using Weavie.Core.Remote;
using Weavie.WorktreeServe;

if (args.Length == 1 && args[0] is "--help" or "-h") {
	Console.WriteLine(WorktreeServeOptions.Usage);
	return 0;
}

var (options, error) = WorktreeServeOptions.Resolve(args);
if (options is null) {
	Console.Error.WriteLine($"[worktree-serve] {error}");
	Console.Error.WriteLine(WorktreeServeOptions.Usage);
	return 1;
}

using var shutdown = new CancellationTokenSource();
void Cancel(object? sender, ConsoleCancelEventArgs eventArgs) {
	eventArgs.Cancel = true;
	shutdown.Cancel();
}
Console.CancelKeyPress += Cancel;
using var termination = OperatingSystem.IsWindows()
	? null
	: PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => {
		context.Cancel = true;
		shutdown.Cancel();
	});
try {
	await new WorktreeServeApp(new TailscaleCli()).RunAsync(options, shutdown.Token).ConfigureAwait(false);
	return 0;
} catch (OperationCanceledException) when (shutdown.IsCancellationRequested) {
	return 0;
} catch (Exception ex) {
	Console.Error.WriteLine($"[worktree-serve] failed: {ex.Message}");
	return 1;
} finally {
	Console.CancelKeyPress -= Cancel;
}
