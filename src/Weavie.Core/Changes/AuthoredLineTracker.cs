using System.Collections.ObjectModel;
using Weavie.Core.Workspaces;

namespace Weavie.Core.Changes;

/// <summary>A current manually-authored line, numbered as Monaco numbers lines.</summary>
/// <param name="Number">The 1-based line number.</param>
/// <param name="Text">The current line text, without its line ending.</param>
public readonly record struct AuthoredLine(int Number, string Text);

/// <summary>An immutable point-in-time view of one file's manually-authored lines.</summary>
public sealed class AuthoredLineSnapshot {
	internal AuthoredLineSnapshot(string path, long version, IReadOnlyList<AuthoredLine> lines) {
		Path = path;
		Version = version;
		Lines = lines;
	}

	/// <summary>The canonical file path the snapshot describes.</summary>
	public string Path { get; }

	/// <summary>The per-file version for this exact line set.</summary>
	public long Version { get; }

	/// <summary>The current manually-authored lines, in ascending line-number order.</summary>
	public IReadOnlyList<AuthoredLine> Lines { get; }
}

/// <summary>
/// Mirrors editor-observed text and keeps only the lines introduced or replaced by a manual write. It never reads
/// the filesystem: callers feed successful reads and writes, then take an immutable snapshot for spelling.
/// </summary>
public sealed class AuthoredLineTracker {
	private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
		? StringComparer.OrdinalIgnoreCase
		: StringComparer.Ordinal;

	private readonly object _gate = new();
	private readonly Dictionary<string, Entry> _entries = new(PathComparer);

	/// <summary>Reconciles an externally read file: matching lines retain authorship and all external changes clear it.</summary>
	public void OnRead(string path, string text) => Reconcile(CanonicalPath(path), text, authoredChanges: false);

	/// <summary>Reconciles a successful manual write: matching lines retain authorship and its changes become authored.</summary>
	public void OnWrite(string path, string text) => Reconcile(CanonicalPath(path), text, authoredChanges: true);

	/// <summary>
	/// Reconciles a scratch buffer with its saved content as a manual write, then transfers that exact state to its
	/// target path and removes the scratch entry.
	/// </summary>
	public void Move(string sourceScratchPath, string targetPath, string content) {
		string source = CanonicalPath(sourceScratchPath);
		string target = CanonicalPath(targetPath);
		ArgumentNullException.ThrowIfNull(content);

		lock (_gate) {
			ReconcileLocked(source, content, authoredChanges: true);
			if (PathComparer.Equals(source, target)) {
				return;
			}

			var sourceEntry = _entries[source];
			long priorVersion = _entries.TryGetValue(target, out var targetEntry)
				? Math.Max(sourceEntry.Version, targetEntry.Version)
				: sourceEntry.Version;
			_entries[target] = new Entry(sourceEntry.Text, (bool[])sourceEntry.Authored.Clone(), checked(priorVersion + 1));
			_entries.Remove(source);
		}
	}

	/// <summary>Stops tracking a file that was discarded or deleted.</summary>
	public void Forget(string path) {
		string canonicalPath = CanonicalPath(path);
		lock (_gate) {
			_entries.Remove(canonicalPath);
		}
	}

	/// <summary>Returns an immutable snapshot for <paramref name="path"/>, or <see langword="null"/> before any read or write.</summary>
	public AuthoredLineSnapshot? Snapshot(string path) {
		string canonicalPath = CanonicalPath(path);
		lock (_gate) {
			return _entries.TryGetValue(canonicalPath, out var entry)
				? BuildSnapshot(canonicalPath, entry)
				: null;
		}
	}

	private void Reconcile(string path, string text, bool authoredChanges) {
		ArgumentNullException.ThrowIfNull(text);
		lock (_gate) {
			ReconcileLocked(path, text, authoredChanges);
		}
	}

	private void ReconcileLocked(string path, string text, bool authoredChanges) {
		string[] afterLines = LineDiff.SplitLines(text);
		if (!_entries.TryGetValue(path, out var entry)) {
			_entries[path] = new Entry(text, [.. Enumerable.Repeat(authoredChanges, afterLines.Length)], version: 1);
			return;
		}

		bool[] authored = Rebase(entry.Text, entry.Authored, afterLines, authoredChanges);
		if (string.Equals(entry.Text, text, StringComparison.Ordinal) && entry.Authored.AsSpan().SequenceEqual(authored)) {
			return;
		}

		entry.Text = text;
		entry.Authored = authored;
		entry.Version = checked(entry.Version + 1);
	}

	private static bool[] Rebase(string beforeText, IReadOnlyList<bool> beforeAuthored, IReadOnlyList<string> afterLines, bool authoredChanges) {
		string[] beforeLines = LineDiff.SplitLines(beforeText);
		if (beforeAuthored.Count != beforeLines.Length) {
			throw new InvalidOperationException("The authored-line mask does not match its source text.");
		}

		bool[] authored = new bool[afterLines.Count];
		int before = 0;
		int after = 0;
		foreach (var op in LineHunker.Align(beforeLines, afterLines)) {
			switch (op.Kind) {
				case LineHunker.LineOpKind.Equal:
					authored[after++] = beforeAuthored[before++];
					break;
				case LineHunker.LineOpKind.Delete:
					before++;
					break;
				case LineHunker.LineOpKind.Insert:
					authored[after++] = authoredChanges;
					break;
			}
		}
		if (before != beforeLines.Length || after != afterLines.Count) {
			throw new InvalidOperationException("The line alignment did not consume both authored-line texts.");
		}

		return authored;
	}

	private static AuthoredLineSnapshot BuildSnapshot(string path, Entry entry) {
		string[] lines = LineDiff.SplitLines(entry.Text);
		var authored = new List<AuthoredLine>();
		for (int index = 0; index < lines.Length; index++) {
			if (entry.Authored[index]) {
				authored.Add(new AuthoredLine(index + 1, lines[index]));
			}
		}

		return new AuthoredLineSnapshot(path, entry.Version, new ReadOnlyCollection<AuthoredLine>(authored));
	}

	private static string CanonicalPath(string path) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		return WorkspacePaths.CanonicalFsPath(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
	}

	private sealed class Entry(string text, bool[] authored, long version) {
		public string Text = text;
		public bool[] Authored = authored;
		public long Version = version;
	}
}
