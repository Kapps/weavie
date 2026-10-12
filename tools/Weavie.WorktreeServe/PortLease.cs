namespace Weavie.WorktreeServe;

internal sealed class PortLease : IDisposable {
	private readonly FileStream _stream;

	private PortLease(FileStream stream) {
		_stream = stream;
	}

	public static PortLease Acquire(int port, string lockDirectory) {
		string path = Path.Combine(lockDirectory, $"weavie-worktree-serve-{port}.lock");
		return Acquire(path, $"another Weavie worktree preview owns HTTPS port {port}.");
	}

	public static PortLease AcquireState(string stateRoot) {
		string path = Path.Combine(stateRoot, ".worktree-serve.lock");
		return Acquire(path, $"another Weavie worktree preview owns state root {stateRoot}.");
	}

	// The lock file outlives the lease: deleting it on release would let a waiter lock the unlinked file while a
	// newcomer locks a fresh one at the same path.
	private static PortLease Acquire(string path, string message) {
		try {
			return new PortLease(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
		} catch (IOException ex) {
			throw new InvalidOperationException(message, ex);
		}
	}

	public void Dispose() => _stream.Dispose();
}
