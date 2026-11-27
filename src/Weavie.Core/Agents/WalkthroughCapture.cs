using Weavie.Core.FileSystem;

namespace Weavie.Core.Agents;

/// <summary>Reads the files a session's walkthroughs cite. Reads only the cited files, never the tree.</summary>
public sealed class WalkthroughCapture {
	private readonly IFileSystem _fileSystem;
	private readonly Func<string, bool> _hasUnkeptAgentEdits;

	/// <summary>
	/// Captures from <paramref name="worktreeRoot"/>; <paramref name="hasUnkeptAgentEdits"/> answers, for an absolute
	/// path, whether the file holds agent edits the user has not kept.
	/// </summary>
	public WalkthroughCapture(IFileSystem fileSystem, string worktreeRoot, Func<string, bool> hasUnkeptAgentEdits) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrEmpty(worktreeRoot);
		ArgumentNullException.ThrowIfNull(hasUnkeptAgentEdits);
		_fileSystem = fileSystem;
		Root = Path.GetFullPath(worktreeRoot);
		_hasUnkeptAgentEdits = hasUnkeptAgentEdits;
	}

	/// <summary>The worktree cited paths are relative to.</summary>
	public string Root { get; }

	/// <summary>A capture with no worktree, for a server that can't show walkthroughs: every path is refused.</summary>
	public static WalkthroughCapture None { get; } = new(new InMemoryFileSystem(), Path.GetTempPath(), _ => false);

	/// <summary>The reads of one tool call, in which each cited file is read once.</summary>
	public WalkthroughReads Begin() => new(this);

	internal string? TryRead(string path, out CitedFile cited) {
		cited = new CitedFile(new WorktreeTextFile(string.Empty, string.Empty), false);
		if (WorktreeTextFile.TryRead(_fileSystem, Root, path, null, AgentWalkthroughLimits.FileBytes, out var file) is { } error) return error;
		cited = new CitedFile(file, _hasUnkeptAgentEdits(Path.Combine(Root, file.Path)));
		return null;
	}
}

/// <summary>A cited file and whether the agent had already edited it, unkept, when it was read.</summary>
public sealed record CitedFile(WorktreeTextFile File, bool Late);

/// <summary>One tool call's reads of cited files.</summary>
public sealed class WalkthroughReads {
	private readonly WalkthroughCapture _capture;
	private readonly Dictionary<string, (CitedFile File, string? Error)> _read = new(StringComparer.Ordinal);

	internal WalkthroughReads(WalkthroughCapture capture) {
		_capture = capture;
	}

	/// <summary>The worktree cited paths are relative to.</summary>
	public string Root => _capture.Root;

	/// <summary>The file <paramref name="path"/> names; refused, under <paramref name="at"/>, when it can't be cited.</summary>
	public CitedFile Cite(string path, string at) {
		var (file, error) = Read(path);
		return error is null ? file : AgentBlockRules.Fail<CitedFile>($"{at}: {error}");
	}

	/// <summary>The file as it stands now, or null when it can no longer be read.</summary>
	public CitedFile? Record(string path) => Read(path) is { Error: null } read ? read.File : null;

	private (CitedFile File, string? Error) Read(string path) {
		if (!_read.TryGetValue(path, out var read)) {
			string? error = _capture.TryRead(path, out var file);
			_read[path] = read = (file, error);
		}
		return read;
	}
}
