using System.Threading.Channels;

namespace Weavie.Hosting;

/// <summary>Invalidates Git status when one worktree's index, HEAD, or refs change.</summary>
internal sealed class GitMetadataWatcher {
	private readonly Channel<Exception> _failures = Channel.CreateUnbounded<Exception>(
		new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
	private readonly IReadOnlyList<FileSystemWatcher> _watchers;
	private readonly Action<Exception> _onFailure;
	private string? _currentRef;

	public GitMetadataWatcher(
		SessionTaskScope background,
		string worktree,
		Action invalidate,
		Action<Exception> onFailure) {
		ArgumentNullException.ThrowIfNull(background);
		ArgumentException.ThrowIfNullOrEmpty(worktree);
		ArgumentNullException.ThrowIfNull(invalidate);
		ArgumentNullException.ThrowIfNull(onFailure);

		_onFailure = onFailure;
		_watchers = CreateWatchers(worktree, invalidate);
		_ = background.Run(RunAsync);
	}

	private async Task RunAsync(CancellationToken ct) {
		try {
			var failure = new IOException(
				"Git metadata watching failed.",
				await _failures.Reader.ReadAsync(ct).ConfigureAwait(false));
			_onFailure(failure);
			throw failure;
		} finally {
			foreach (var watcher in _watchers) {
				watcher.Dispose();
			}
		}
	}

	private IReadOnlyList<FileSystemWatcher> CreateWatchers(string worktree, Action invalidate) {
		string dotGit = Path.Combine(worktree, ".git");
		if (!Directory.Exists(dotGit) && !File.Exists(dotGit)) {
			return [];
		}

		string gitDirectory = Directory.Exists(dotGit)
			? dotGit
			: ResolvePointer(dotGit, "gitdir:", worktree);
		string commonDirectory = File.Exists(Path.Combine(gitDirectory, "commondir"))
			? ResolvePointer(Path.Combine(gitDirectory, "commondir"), "", gitDirectory)
			: gitDirectory;
		RefreshCurrentRef(gitDirectory, commonDirectory);

		var watchers = new List<FileSystemWatcher> {
			Create(gitDirectory, "HEAD", includeSubdirectories: false, _ => {
				try {
					RefreshCurrentRef(gitDirectory, commonDirectory);
				} catch (IOException error) {
					_failures.Writer.TryWrite(error);
					return;
				}
				invalidate();
			}),
			Create(gitDirectory, "index", includeSubdirectories: false, _ => invalidate()),
			Create(commonDirectory, "packed-refs", includeSubdirectories: false, _ => invalidate()),
		};

		string refs = Path.Combine(commonDirectory, "refs");
		if (Directory.Exists(refs)) {
			watchers.Add(Create(refs, "*", includeSubdirectories: true, path => {
				if (PathEquals(path, Volatile.Read(ref _currentRef))) {
					invalidate();
				}
			}));
		}

		return watchers;
	}

	private FileSystemWatcher Create(
		string directory,
		string filter,
		bool includeSubdirectories,
		Action<string> invalidate) {
		var watcher = new FileSystemWatcher(directory, filter) {
			IncludeSubdirectories = includeSubdirectories,
			NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
		};
		watcher.Changed += (_, e) => invalidate(e.FullPath);
		watcher.Created += (_, e) => invalidate(e.FullPath);
		watcher.Deleted += (_, e) => invalidate(e.FullPath);
		watcher.Renamed += (_, e) => invalidate(e.FullPath);
		watcher.Error += (_, e) => _failures.Writer.TryWrite(e.GetException());
		watcher.EnableRaisingEvents = true;
		return watcher;
	}

	private void RefreshCurrentRef(string gitDirectory, string commonDirectory) {
		string head = File.ReadAllText(Path.Combine(gitDirectory, "HEAD")).Trim();
		const string prefix = "ref:";
		Volatile.Write(
			ref _currentRef,
			head.StartsWith(prefix, StringComparison.Ordinal)
				? Path.GetFullPath(head[prefix.Length..].Trim(), commonDirectory)
				: null);
	}

	private static bool PathEquals(string path, string? other) =>
		other is not null && string.Equals(
			Path.GetFullPath(path),
			other,
			OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

	private static string ResolvePointer(string path, string prefix, string relativeTo) {
		string value = File.ReadAllText(path).Trim();
		if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
			throw new IOException($"Invalid Git metadata pointer: {path}");
		}

		return Path.GetFullPath(value[prefix.Length..].Trim(), relativeTo);
	}
}
