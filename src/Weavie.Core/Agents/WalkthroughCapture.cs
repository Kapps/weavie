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

	internal string? TryRead(string path, out WorktreeTextFile file, out bool late) {
		late = false;
		if (WorktreeTextFile.TryRead(_fileSystem, Root, path, null, AgentWalkthroughLimits.FileBytes, out file) is { } error) return error;
		late = _hasUnkeptAgentEdits(Path.Combine(Root, file.Path));
		return null;
	}
}

/// <summary>One tool call's reads of cited files: each read once, and the copies of them to keep.</summary>
public sealed class WalkthroughReads {
	private readonly WalkthroughCapture _capture;
	private readonly Dictionary<string, (WorktreeTextFile File, bool Late, string? Error)> _read = new(StringComparer.Ordinal);
	private readonly Dictionary<string, string> _sources = new(StringComparer.Ordinal);
	private readonly List<AgentWalkthroughFile> _files = [];

	internal WalkthroughReads(WalkthroughCapture capture) {
		_capture = capture;
	}

	/// <summary>The worktree cited paths are relative to.</summary>
	public string Root => _capture.Root;

	/// <summary>The copies of the files read, by hash.</summary>
	public IReadOnlyDictionary<string, string> Sources => _sources;

	/// <summary>Each file cited, as first read.</summary>
	public IReadOnlyList<AgentWalkthroughFile> Files => _files;

	/// <summary>The file <paramref name="path"/> names; refused, under <paramref name="at"/>, when it can't be cited.</summary>
	public WorktreeTextFile Cite(string path, string at) {
		var (file, late, error) = Read(path);
		if (error is not null) return AgentBlockRules.Fail<WorktreeTextFile>($"{at}: {error}");
		if (_files.All(cited => cited.Path != file.Path)) _files.Add(new AgentWalkthroughFile { Path = file.Path, Before = file.Hash, Late = late });
		return Keep(file);
	}

	/// <summary>The file as it stands now, or null when it can no longer be read.</summary>
	public WorktreeTextFile? Record(string path) => Read(path) is { Error: null } read ? Keep(read.File) : null;

	private WorktreeTextFile Keep(WorktreeTextFile file) {
		_sources[file.Hash] = file.Text;
		return file;
	}

	private (WorktreeTextFile File, bool Late, string? Error) Read(string path) {
		if (!_read.TryGetValue(path, out var read)) {
			string? error = _capture.TryRead(path, out var file, out bool late);
			_read[path] = read = (file, late, error);
		}
		return read;
	}
}
