using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Sessions;

/// <summary>
/// Persists the durable subset of one structured agent pane's <see cref="AgentPaneMessage"/> stream, so the
/// rendered pane restores across a reload, a session unload/reload, and a worker restart — the structured pane's
/// analogue to the shell's <see cref="Terminal.ScrollbackLog"/>. Only terminal, meaningful messages are kept
/// (see <see cref="IsPersistable"/>); volatile lifecycle/pending/streaming updates are live-only. Written as
/// append-only JSONL (one message per line — the transcript is unbounded, so a full rewrite per message would be
/// O(n²) on the pane's hot path) to an owner-only file (a transcript can carry command output or file contents).
/// </summary>
public sealed class AgentPaneTranscriptStore {
	// Compact (single-line) so each message is exactly one JSONL record.
	private static readonly JsonSerializerOptions JsonOptions =
		new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

	private readonly IFileSystem _fileSystem;
	private readonly Lock _gate = new();
	private readonly List<AgentPaneMessage> _messages;

	/// <summary>Creates the store over <paramref name="path"/>, loading any existing transcript now.</summary>
	public AgentPaneTranscriptStore(IFileSystem fileSystem, string path) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrEmpty(path);
		_fileSystem = fileSystem;
		FilePath = path;
		lock (_gate) {
			_messages = LoadLocked();
		}
	}

	/// <summary>Diagnostic log line for read, parse, and persist failures.</summary>
	public event Action<string>? Log;

	/// <summary>The file backing this store.</summary>
	public string FilePath { get; }

	/// <summary>The persisted messages, in order — replayed to seed a (re)opened pane.</summary>
	public IReadOnlyList<AgentPaneMessage> Snapshot() {
		lock (_gate) {
			return [.. _messages];
		}
	}

	/// <summary>Records <paramref name="message"/> when it belongs in the durable transcript; a no-op otherwise.</summary>
	public void Append(AgentPaneMessage message) {
		ArgumentNullException.ThrowIfNull(message);
		if (!IsPersistable(message)) {
			return;
		}

		lock (_gate) {
			_messages.Add(message);
			try {
				_fileSystem.AppendAllText(FilePath, JsonSerializer.Serialize(message, JsonOptions) + "\n");
				// A transcript can echo command output or file contents; keep it owner-only on POSIX.
				SecureFile.Restrict(FilePath);
			} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
				Log?.Invoke($"[agent-pane] could not persist: {ex.Message}");
			}
		}
	}

	/// <summary>Forgets the whole transcript (a fresh thread) and removes the backing file.</summary>
	public void Clear() {
		lock (_gate) {
			if (_messages.Count == 0 && !_fileSystem.FileExists(FilePath)) {
				return;
			}

			_messages.Clear();
			try {
				_fileSystem.DeleteFile(FilePath);
			} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
				Log?.Invoke($"[agent-pane] could not clear {FilePath}: {ex.Message}");
			}
		}
	}

	/// <summary>
	/// Whether <paramref name="message"/> is durable conversation rather than live-only chrome. Completed items
	/// (the results and narration) and submitted user turns carry the conversation; lifecycle, in-progress items,
	/// pending prompts (unanswerable once their process is gone), drafts, diffs, and transient launch/stderr
	/// warnings/errors (regenerated live on each launch) do not.
	/// </summary>
	public static bool IsPersistable(AgentPaneMessage message) {
		ArgumentNullException.ThrowIfNull(message);
		return message.Type switch {
			"user-message" or "user-steer" => true,
			"user-image" => string.Equals(message.Status, "submitted", StringComparison.Ordinal),
			"item-completed" => true,
			"interrupted" => true,
			_ => false,
		};
	}

	private List<AgentPaneMessage> LoadLocked() {
		if (!_fileSystem.FileExists(FilePath)) {
			return [];
		}

		string text;
		try {
			text = _fileSystem.ReadAllText(FilePath);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[agent-pane] could not read {FilePath}: {ex.Message}; starting empty");
			return [];
		}

		// JSONL is line-resilient: a torn last line (a crash mid-append) or a stale-schema record is skipped, not
		// fatal, so one bad line never discards the rest of the transcript.
		List<AgentPaneMessage> messages = [];
		foreach (string line in text.Split('\n')) {
			if (string.IsNullOrWhiteSpace(line)) {
				continue;
			}

			try {
				if (JsonSerializer.Deserialize<AgentPaneMessage>(line, JsonOptions) is { } message) {
					messages.Add(message);
				}
			} catch (JsonException ex) {
				Log?.Invoke($"[agent-pane] skipping malformed line in {FilePath}: {ex.Message}");
			}
		}

		return messages;
	}
}
