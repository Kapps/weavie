using System.Text;
using System.Text.Json;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Corrections;

/// <summary>
/// The per-workspace ring of recorded corrections, persisted as JSONL (one <see cref="CorrectionRecord"/>
/// per line, oldest first) at <c>~/.weavie/workspaces/&lt;id&gt;/corrections.jsonl</c>. Byte-capped because
/// the whole ring feeds one <c>/learn</c> analysis and must fit the model's window: appending past
/// <see cref="MaxBytes"/> evicts whole oldest lines (the one sanctioned silent cap here — a best-effort
/// learning corpus deliberately biased toward recent corrections), and per-entry ceilings keep one monster
/// turn from evicting all history behind it. Shared by every session of the workspace; all members lock.
/// </summary>
public sealed class CorrectionCorpus {
	/// <summary>The whole-ring byte cap — the /learn context budget, not user config.</summary>
	public const int MaxBytes = 96 * 1024;

	// One entry may take at most a quarter of the ring, so a single huge turn keeps ≥3 entries of history.
	private const int MaxEntryBytes = MaxBytes / 4;
	private const int MaxPromptBytes = 2 * 1024;
	private const int MaxFileDeltaBytes = 8 * 1024;
	private const string TruncationMarker = "…[truncated]";

	private readonly IFileSystem _fileSystem;
	private readonly Lock _gate = new();
	private readonly List<string> _lines = [];
	private int _bytes;

	/// <summary>Creates the corpus over <paramref name="path"/>, loading any persisted ring now.</summary>
	/// <param name="fileSystem">The filesystem the ring persists through.</param>
	/// <param name="path">The backing JSONL file.</param>
	public CorrectionCorpus(IFileSystem fileSystem, string path) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrEmpty(path);
		_fileSystem = fileSystem;
		FilePath = path;
		lock (_gate) {
			LoadLocked();
		}
	}

	/// <summary>The file backing this ring.</summary>
	public string FilePath { get; }

	/// <summary>Diagnostic log line — read/persist failures on the best-effort ring.</summary>
	public event Action<string>? Log;

	/// <summary>Raised after the ring's entry count changed — an <see cref="Append"/>, a fresh-appending <see cref="Coalesce"/>, a <see cref="Remove"/>, or a <see cref="Take"/> (fires outside the lock). An in-place <see cref="Coalesce"/> replace leaves the count unchanged and does not fire.</summary>
	public event Action? Changed;

	/// <summary>How many corrections the ring currently holds.</summary>
	public int Count {
		get {
			lock (_gate) {
				return _lines.Count;
			}
		}
	}

	/// <summary>
	/// Appends <paramref name="record"/> (truncated to the per-entry ceilings), evicts oldest lines while over
	/// <see cref="MaxBytes"/>, and persists the ring. Returns the stored line so a follow-up
	/// <see cref="Coalesce"/> can supersede it.
	/// </summary>
	/// <param name="record">The correction to append.</param>
	public string Append(CorrectionRecord record) {
		ArgumentNullException.ThrowIfNull(record);
		string line = Serialize(Bound(record));
		lock (_gate) {
			AddLocked(line);
		}

		Changed?.Invoke();
		return line;
	}

	/// <summary>
	/// Replaces <paramref name="previousLine"/> (the line a prior <see cref="Append"/>/<see cref="Coalesce"/>
	/// returned) with <paramref name="record"/> in place, so successive editor saves over one agent region evolve a
	/// single entry instead of piling up intermediate ones. When that line is gone (evicted or already consumed by
	/// <c>/learn</c>) this appends fresh. Returns the new stored line. Count is unchanged on a replace, so the nudge
	/// (which re-evaluates on <see cref="Changed"/>) is not disturbed per keystroke — only a fresh append fires it.
	/// </summary>
	/// <param name="record">The superseding correction.</param>
	/// <param name="previousLine">The line to replace.</param>
	public string Coalesce(CorrectionRecord record, string previousLine) {
		ArgumentNullException.ThrowIfNull(record);
		ArgumentNullException.ThrowIfNull(previousLine);
		string line = Serialize(Bound(record));
		bool appended = false;
		lock (_gate) {
			int index = _lines.LastIndexOf(previousLine);
			if (index < 0) {
				AddLocked(line);
				appended = true;
			} else {
				_bytes += Encoding.UTF8.GetByteCount(line) - Encoding.UTF8.GetByteCount(_lines[index]);
				_lines[index] = line;
				EvictLocked();
				PersistLocked();
			}
		}

		if (appended) {
			Changed?.Invoke();
		}

		return line;
	}

	/// <summary>
	/// Removes <paramref name="line"/> (a line a prior <see cref="Append"/>/<see cref="Coalesce"/> returned) when a
	/// running correction was retyped back to the agent's own output, so nothing is left to learn from. A no-op when
	/// the line is already gone.
	/// </summary>
	/// <param name="line">The line to drop.</param>
	public void Remove(string line) {
		ArgumentNullException.ThrowIfNull(line);
		lock (_gate) {
			int index = _lines.LastIndexOf(line);
			if (index < 0) {
				return;
			}

			_bytes -= Encoding.UTF8.GetByteCount(_lines[index]) + 1;
			_lines.RemoveAt(index);
			PersistLocked();
		}

		Changed?.Invoke();
	}

	private void AddLocked(string line) {
		_lines.Add(line);
		_bytes += Encoding.UTF8.GetByteCount(line) + 1;
		EvictLocked();
		PersistLocked();
	}

	private void EvictLocked() {
		while (_bytes > MaxBytes && _lines.Count > 1) {
			_bytes -= Encoding.UTF8.GetByteCount(_lines[0]) + 1;
			_lines.RemoveAt(0);
		}
	}

	/// <summary>The recorded corrections, oldest first.</summary>
	public IReadOnlyList<CorrectionRecord> ReadAll() {
		lock (_gate) {
			var records = new List<CorrectionRecord>(_lines.Count);
			foreach (string line in _lines) {
				if (TryParse(line) is { } record) {
					records.Add(record);
				}
			}

			return records;
		}
	}

	/// <summary>
	/// Atomically returns every recorded correction (oldest first) and empties the ring — the /learn consume
	/// point. Read-and-clear under one lock so a correction another session appends mid-/learn is either
	/// returned here (and analyzed) or lands afterward and stays in the ring; it can never be evicted between
	/// a separate read and a positional clear. Returns empty (and raises nothing) when the ring is empty.
	/// </summary>
	public IReadOnlyList<CorrectionRecord> Take() {
		List<CorrectionRecord> records;
		lock (_gate) {
			if (_lines.Count == 0) {
				return [];
			}

			records = new List<CorrectionRecord>(_lines.Count);
			foreach (string line in _lines) {
				if (TryParse(line) is { } record) {
					records.Add(record);
				}
			}

			_lines.Clear();
			_bytes = 0;
			PersistLocked();
		}

		Changed?.Invoke();
		return records;
	}

	// Applies the per-entry ceilings: prompt and per-file delta byte caps, then whole-entry trimming that
	// drops trailing files (counted in DroppedFiles) rather than fail — best-effort, never lossy silently.
	// Everything is sanitized first, so the ring never stores control bytes (see Sanitize).
	private static CorrectionRecord Bound(CorrectionRecord record) {
		var files = record.Files
			.Select(f => new CorrectionFile { Path = Sanitize(f.Path), Delta = TruncateUtf8(Sanitize(f.Delta), MaxFileDeltaBytes) })
			.ToList();
		var bounded = record with { Prompt = record.Prompt is { } p ? TruncateUtf8(Sanitize(p), MaxPromptBytes) : null, Files = files };
		while (files.Count > 1 && Encoding.UTF8.GetByteCount(Serialize(bounded)) > MaxEntryBytes) {
			files.RemoveAt(files.Count - 1);
			bounded = bounded with { Files = files, DroppedFiles = record.Files.Count - files.Count };
		}

		// JSON escaping can inflate the one remaining delta past the ceiling; shrink until the LINE fits, so
		// the "one entry keeps ≥3 entries of history" guarantee holds for any content.
		for (int budget = MaxFileDeltaBytes / 2;
			budget >= 256 && Encoding.UTF8.GetByteCount(Serialize(bounded)) > MaxEntryBytes;
			budget /= 2) {
			files[0] = files[0] with { Delta = TruncateUtf8(files[0].Delta, budget) };
			bounded = bounded with { Files = files };
		}

		return bounded;
	}

	// The ring stores printable text plus \n and \t only: every other control char (C0/C1 incl. ESC, DEL, CR)
	// is stripped at append, so corpus content replayed into /learn's bracketed paste can never carry an
	// escape sequence that terminates the paste and turns the remainder into typed PTY input.
	private static string Sanitize(string text) {
		if (!text.Any(IsStripped)) {
			return text;
		}

		var sb = new StringBuilder(text.Length);
		foreach (char c in text) {
			if (!IsStripped(c)) {
				sb.Append(c);
			}
		}

		return sb.ToString();

		static bool IsStripped(char c) => char.IsControl(c) && c is not ('\n' or '\t');
	}

	// Cuts text to at most maxBytes of UTF-8 (never splitting a surrogate pair), marking the cut.
	private static string TruncateUtf8(string text, int maxBytes) {
		if (Encoding.UTF8.GetByteCount(text) <= maxBytes) {
			return text;
		}

		int budget = maxBytes - Encoding.UTF8.GetByteCount(TruncationMarker);
		int bytes = 0;
		int end = 0;
		for (int i = 0; i < text.Length;) {
			bool pair = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
			int size = pair ? 4 : Encoding.UTF8.GetByteCount(text, i, 1);
			if (bytes + size > budget) {
				break;
			}

			bytes += size;
			i += pair ? 2 : 1;
			end = i;
		}

		return text[..end] + TruncationMarker;
	}

	private static string Serialize(CorrectionRecord record) => JsonSerializer.Serialize(record);

	private static CorrectionRecord? TryParse(string line) {
		try {
			return JsonSerializer.Deserialize<CorrectionRecord>(line);
		} catch (JsonException) {
			return null;
		}
	}

	private void LoadLocked() {
		if (!_fileSystem.FileExists(FilePath)) {
			return;
		}

		string text;
		try {
			text = _fileSystem.ReadAllText(FilePath);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[corrections] could not read {FilePath}: {ex.Message}; starting empty");
			return;
		}

		foreach (string line in text.Split('\n')) {
			// A line that no longer parses is dropped from the best-effort ring rather than wedging it.
			if (line.Length > 0 && TryParse(line) is not null) {
				_lines.Add(line);
				_bytes += Encoding.UTF8.GetByteCount(line) + 1;
			}
		}
	}

	private void PersistLocked() {
		try {
			_fileSystem.WriteAllTextAtomic(FilePath, _lines.Count == 0 ? string.Empty : string.Join('\n', _lines) + "\n");
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[corrections] could not persist the ring: {ex.Message}");
		}
	}
}
