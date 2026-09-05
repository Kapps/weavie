using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Sessions;

/// <summary>
/// How the next <c>claude</c> launch in a working directory should be wired: its stable session id (never
/// empty) and whether to <c>--resume</c> or create it fresh with <c>--session-id</c>. The resume flag is
/// derived at launch from whether Claude has a transcript for the id on disk, not stored — see
/// <see cref="ClaudeSessionStore"/>.
/// </summary>
/// <param name="SessionId">The UUID Weavie owns for this working directory's Claude conversation.</param>
/// <param name="Resume">True to reattach (<c>--resume</c>); false to create it (<c>--session-id</c>).</param>
public readonly record struct ClaudeLaunch(string SessionId, bool Resume);

/// <summary>
/// Remembers the Claude Code session id Weavie assigned each working directory (keyed by launch directory),
/// persisted atomically to <c>~/.weavie/claude-sessions.json</c>, so reopening resumes the previous
/// conversation. Weavie assigns the id as <c>--session-id</c> rather than scraping Claude's storage, so
/// resume is deterministic. Whether a launch resumes or re-creates the id is not tracked here — it is decided
/// from whether Claude's transcript for the id exists on disk (<see cref="IClaudeTranscripts"/>), the same
/// thing <c>claude</c> itself checks, so the two can never drift apart (a stored "started" bit could, and a
/// stale one made a relaunch re-create an id whose conversation still existed → "Session ID … is already in
/// use"). A malformed file is backed up to <c>claude-sessions.json.bad</c> and reset.
/// </summary>
public sealed class ClaudeSessionStore : JsonDocumentStore {
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	private List<Entry> _items = [];

	/// <summary>Creates the store over <paramref name="path"/>, loading it now.</summary>
	/// <param name="fileSystem">The filesystem the ids persist through.</param>
	/// <param name="path">The backing file.</param>
	public ClaudeSessionStore(IFileSystem fileSystem, string path) : base(fileSystem, path) {
		Load();
	}

	/// <summary>
	/// Returns the stable Claude session id for <paramref name="workingDirectory"/>, minting and persisting one
	/// on first use so it is known up front and resume is deterministic. Whether the next launch <c>--resume</c>s
	/// or re-creates this id is decided at launch from the transcript on disk, not here.
	/// </summary>
	public string Resolve(string workingDirectory) {
		ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
		string key = PathIdentity.Normalize(workingDirectory);
		lock (Gate) {
			var entry = Find(key);
			if (entry is null) {
				entry = new Entry { Key = key, Id = Guid.NewGuid().ToString() };
				_items.Add(entry);
				PersistLocked();
			}

			return entry.Id;
		}
	}

	/// <summary>
	/// Abandons <paramref name="workingDirectory"/>'s assigned id entirely (next <see cref="Resolve"/>
	/// cold-starts a new one). Used when a launch could not bring the id up at all — its transcript is pruned or
	/// corrupt, or the id is otherwise poison — so there is nothing to preserve.
	/// </summary>
	public void Forget(string workingDirectory) {
		ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
		string key = PathIdentity.Normalize(workingDirectory);
		lock (Gate) {
			if (RemoveLocked(key)) {
				PersistLocked();
			}
		}
	}

	/// <summary>
	/// Drops <paramref name="workingDirectory"/>'s tracked id on a user <c>/clear</c>, so a relaunch cold-starts
	/// instead of reattaching to the stale transcript the clear meant to escape; the next real user message
	/// re-establishes tracking via <see cref="Adopt"/>.
	/// </summary>
	public void Clear(string workingDirectory) {
		ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
		string key = PathIdentity.Normalize(workingDirectory);
		lock (Gate) {
			if (RemoveLocked(key)) {
				PersistLocked();
			}
		}
	}

	/// <summary>
	/// Records the session id claude reports it's actually in for <paramref name="workingDirectory"/> (observed
	/// off the hook stream on a real user message), realigning the store after claude rotated its id out from
	/// under Weavie (chiefly a <c>/clear</c>). A no-op when the id already matches, so the normal flow never
	/// thrashes the file.
	/// </summary>
	public void Adopt(string workingDirectory, string sessionId) {
		ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
		ArgumentException.ThrowIfNullOrEmpty(sessionId);
		string key = PathIdentity.Normalize(workingDirectory);
		lock (Gate) {
			var entry = Find(key);
			if (entry is null) {
				_items.Add(new Entry { Key = key, Id = sessionId });
				PersistLocked();
				return;
			}

			if (!string.Equals(entry.Id, sessionId, StringComparison.Ordinal)) {
				entry.Id = sessionId;
				PersistLocked();
			}
		}
	}

	/// <inheritdoc/>
	protected override void Restore(string? text) {
		var document = text is null ? null : JsonSerializer.Deserialize<Document>(text);
		_items = document?.Sessions is not { } entries
			? []
			: [.. entries
				.Where(e => !string.IsNullOrWhiteSpace(e.Cwd) && !string.IsNullOrWhiteSpace(e.Id))
				.Select(e => new Entry { Key = e.Cwd, Id = e.Id })];
	}

	/// <inheritdoc/>
	protected override string Render() => JsonSerializer.Serialize(
		new Document {
			Version = 1,
			Sessions = [.. _items.Select(e => new SessionEntry { Cwd = e.Key, Id = e.Id })],
		},
		JsonOptions);

	private Entry? Find(string key) => _items.FirstOrDefault(e => PathIdentity.Equals(e.Key, key));

	private bool RemoveLocked(string key) => _items.RemoveAll(e => PathIdentity.Equals(e.Key, key)) > 0;



	private sealed class Entry {
		public required string Key { get; init; }
		public required string Id { get; set; }
	}

	private sealed class Document {
		[JsonPropertyName("version")]
		public int Version { get; set; }

		[JsonPropertyName("sessions")]
		public List<SessionEntry> Sessions { get; set; } = [];
	}

	private sealed class SessionEntry {
		[JsonPropertyName("cwd")]
		public string Cwd { get; set; } = string.Empty;

		[JsonPropertyName("id")]
		public string Id { get; set; } = string.Empty;
	}
}
