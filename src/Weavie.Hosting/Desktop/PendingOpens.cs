namespace Weavie.Hosting.Desktop;

/// <summary>
/// Paths waiting for a page that can receive them. A cold launch resolves its workspace from the path itself,
/// so an open routinely arrives before anything is listening, and a broadcast with no client is dropped
/// rather than buffered.
/// </summary>
public sealed class PendingOpens {
	private readonly Lock _gate = new();
	private readonly List<string> _paths = [];
	private bool _pageReady;

	/// <summary>Holds <paramref name="path"/> until <see cref="Drain"/> can deliver it.</summary>
	public void Add(string path) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		lock (_gate) {
			_paths.Add(path);
		}
	}

	/// <summary>Records that a page can now receive pushes.</summary>
	public void MarkPageReady() {
		lock (_gate) {
			_pageReady = true;
		}
	}

	/// <summary>Takes everything deliverable, or nothing while no page is attached yet.</summary>
	public IReadOnlyList<string> Drain() {
		lock (_gate) {
			if (!_pageReady || _paths.Count == 0) {
				return [];
			}

			string[] drained = [.. _paths];
			_paths.Clear();
			return drained;
		}
	}
}
