using Weavie.Core.FileSystem;

namespace Weavie.Core.Workflows;

/// <summary>Owns shared folders for a host's sessions, including session creations still in flight.</summary>
public sealed class WorkflowDocumentFolders(string root, Action<string> reportFailure) {
	private readonly Lock _gate = new();
	private readonly string _root = PathIdentity.Normalize(root);
	private readonly Dictionary<string, string> _owners = new(PathIdentity.Comparer);
	private readonly Dictionary<string, int> _leases = new(PathIdentity.Comparer);
	private bool _restored;

	/// <summary>The managed directory for a newly allocated run.</summary>
	public string ForRun(string runId) => Path.Combine(_root, runId);

	/// <summary>Restores all durable owners before collecting orphaned managed folders.</summary>
	public void Restore(IEnumerable<KeyValuePair<string, string>> owners) {
		lock (_gate) {
			foreach (var (owner, directory) in owners) {
				if (!Path.IsPathFullyQualified(directory)) throw new IOException("A saved workflow documents directory is not absolute.");
				_owners[owner] = PathIdentity.Normalize(directory);
			}
			_restored = true;
			if (Directory.Exists(_root)) {
				foreach (string directory in Directory.EnumerateDirectories(_root)) Collect(directory);
			}
		}
	}

	/// <summary>Assigns a durable session reference after its run or session state has been saved.</summary>
	public void Bind(string owner, string? directory) {
		lock (_gate) {
			_owners.Remove(owner, out string? previous);
			if (directory is not null) _owners[owner] = directory;
			if (previous is not null && !PathIdentity.Comparer.Equals(previous, directory)) Collect(previous);
		}
	}

	/// <summary>Keeps the source session's folder alive while a new session is being created.</summary>
	public Lease Acquire(string? owner) {
		lock (_gate) {
			string? directory = owner is null ? null : _owners.GetValueOrDefault(owner);
			if (directory is not null) _leases[directory] = _leases.GetValueOrDefault(directory) + 1;
			return new Lease(this, directory);
		}
	}

	private void Release(string directory) {
		lock (_gate) {
			if (--_leases[directory] == 0) _leases.Remove(directory);
			Collect(directory);
		}
	}

	private void Collect(string directory) {
		if (!_restored || _owners.Values.Contains(directory, PathIdentity.Comparer) || _leases.ContainsKey(directory)) return;
		try {
			directory = PathIdentity.Normalize(directory);
			if (Path.GetDirectoryName(directory) is not { } parent || !PathIdentity.Equals(parent, _root)) {
				throw new IOException($"The folder is outside the managed workflow documents directory {_root}.");
			}
			if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			reportFailure($"Could not clean up unused workflow documents at {directory}: {ex.Message}");
		}
	}

	/// <summary>An in-flight session creation's temporary reference.</summary>
	public sealed class Lease : IDisposable {
		private WorkflowDocumentFolders? _owner;

		internal Lease(WorkflowDocumentFolders owner, string? directory) {
			_owner = owner;
			Directory = directory;
		}

		/// <summary>The inherited folder, or null when the source has none.</summary>
		public string? Directory { get; }

		/// <inheritdoc/>
		public void Dispose() {
			var owner = Interlocked.Exchange(ref _owner, null);
			if (Directory is { } directory) owner?.Release(directory);
		}
	}
}
