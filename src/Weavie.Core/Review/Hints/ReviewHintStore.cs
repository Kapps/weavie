using System.Security.Cryptography;
using System.Text;
using Weavie.Core.Agents;
using Weavie.Core.Changes;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Review.Hints;

/// <summary>
/// One session's agent review findings. The first hint of a new primary-conversation turn replaces every older
/// hint; a turn that reports none keeps them. Reads only the reported file, never the tree.
/// </summary>
public sealed partial class ReviewHintStore : IReviewHintSink {
	private readonly Lock _gate = new();
	private readonly IFileSystem _fileSystem;
	private readonly string _root;
	private readonly string _agent;
	private readonly Action<IReadOnlyList<ReviewHint>> _published;
	private readonly Action<string> _rejected;
	private IReadOnlyList<ReviewHint> _hints = [];
	private long _turn;

	/// <summary>
	/// Restores the worktree's saved hints and publishes them. <paramref name="published"/> receives every snapshot in
	/// order; <paramref name="rejected"/> hears each finding refused while observing agent events, and saved hints
	/// discarded because they couldn't be read.
	/// </summary>
	public ReviewHintStore(
		IFileSystem fileSystem,
		string worktreeRoot,
		IReviewPersistence persistence,
		string agentName,
		Action<IReadOnlyList<ReviewHint>> published,
		Action<string> rejected) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrEmpty(worktreeRoot);
		ArgumentNullException.ThrowIfNull(persistence);
		ArgumentException.ThrowIfNullOrEmpty(agentName);
		ArgumentNullException.ThrowIfNull(published);
		ArgumentNullException.ThrowIfNull(rejected);
		_fileSystem = fileSystem;
		_root = Path.GetFullPath(worktreeRoot);
		_persistence = persistence;
		_agent = agentName;
		_published = published;
		_rejected = rejected;
		Restore();
		_turn = _batch;
		_published(_hints);
	}

	/// <summary>The current hints, in report order.</summary>
	public IReadOnlyList<ReviewHint> Hints { get { lock (_gate) return _hints; } }

	/// <summary>Counts primary-conversation turns and captures reported findings; refusals go to the rejection callback.</summary>
	public void Observe(AgentEvent value) {
		ArgumentNullException.ThrowIfNull(value);
		switch (value) {
			case AgentPromptSubmitted:
				lock (_gate) _turn++;
				break;
			case AgentReviewFindingsReported reported:
				try {
					foreach (string error in Add(reported.Findings, reported.Cwd)) RejectFinding(error);
				} catch (IOException ex) {
					RejectFinding(ex.Message);
				}
				break;
			case AgentReviewFindingsUnreadable unreadable:
				RejectFinding(unreadable.Error);
				break;
		}
	}

	private void RejectFinding(string error) => _rejected($"Couldn't show a review finding: {error}");

	/// <inheritdoc/>
	public IReadOnlyList<string> Add(IReadOnlyList<ReviewFinding> findings, string? cwd) {
		ArgumentNullException.ThrowIfNull(findings);
		var errors = new List<string>();
		var captured = new List<(ReviewFinding Finding, string Path, string Text)>();
		foreach (var finding in findings) {
			if (Capture(finding, cwd, out string relative, out string text) is { } error) errors.Add(error);
			else captured.Add((finding, relative, text));
		}
		if (captured.Count == 0) return errors;

		lock (_gate) {
			long batch = _turn;
			var hints = new List<ReviewHint>(_batch == batch ? _hints : []);
			var sources = new Dictionary<string, string>(StringComparer.Ordinal);
			long nextId = _nextId;
			foreach (var (finding, path, text) in captured) {
				string hash = Hash(text);
				sources[hash] = text;
				hints.Add(new ReviewHint(nextId++, batch, path, finding.Line, finding.Severity, finding.Title,
					finding.Body, finding.Category, _agent, hash, LineDiff.SplitLines(text)[finding.Line - 1]));
			}
			Commit(hints, sources, batch, nextId);
		}
		return errors;
	}

	/// <summary>Removes one hint; an id that is already gone changes nothing.</summary>
	public void Dismiss(long id) {
		lock (_gate) {
			if (_hints.All(hint => hint.Id != id)) return;
			Commit([.. _hints.Where(hint => hint.Id != id)], new Dictionary<string, string>(), _batch, _nextId);
		}
	}

	/// <summary>Removes every hint.</summary>
	public void Clear() {
		lock (_gate) {
			if (_hints.Count == 0) return;
			Commit([], new Dictionary<string, string>(), _batch, _nextId);
		}
	}

	/// <summary>The file text the hints on <paramref name="path"/> (worktree-relative) were captured against.</summary>
	public string Source(string path, string hash) {
		lock (_gate) {
			return _hints.Any(hint => hint.Path == path && hint.SourceHash == hash)
				? _sources[hash]
				: throw new InvalidOperationException($"No review hint on '{path}' was captured from that text.");
		}
	}

	private string? Capture(ReviewFinding finding, string? cwd, out string relative, out string text) {
		relative = text = string.Empty;
		if (finding.Title.Length == 0) return $"A review finding on {finding.Path}:{finding.Line} has no summary.";
		string absolute;
		try {
			absolute = AgentPath.Resolve(finding.Path, cwd, _root);
		} catch (ArgumentException) {
			return $"'{finding.Path}' is not a valid path.";
		}
		relative = Path.GetRelativePath(_root, absolute).Replace('\\', '/');
		if (!PathBoundary.Contains(_root, absolute) || relative == ".") {
			return $"'{finding.Path}' isn't a file inside {_root}.";
		}
		if (!_fileSystem.FileExists(absolute)) return $"'{relative}' doesn't exist.";
		if (!_fileSystem.TryReadAllText(absolute, out text)) return $"'{relative}' isn't a text file.";
		int lines = LineDiff.SplitLines(text).Length;
		return finding.Line < 1 || finding.Line > lines
			? $"Line {finding.Line} is outside '{relative}', which has {lines} line(s)."
			: null;
	}

	private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
