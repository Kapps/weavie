using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Sources;

/// <summary>
/// The host-facing entry point to the source system: owns the registered sources and persists each source's access
/// token (a Notion personal access token) at <c>~/.weavie/sources/&lt;id&gt;.json</c> — <c>{ "token": "ntn_…" }</c>,
/// owner-only — so <c>HostCore</c> drives connect/fetch through one injected dependency. Constructed by
/// <c>HostServices</c>; the headless harness injects a stubbed <see cref="HttpClient"/> + token for deterministic
/// tests. The token file is off the Claude-facing settings surface because it holds a secret.
/// </summary>
public sealed class SourceConnector : ISourceConnector {
	private readonly IReadOnlyList<ISource> _sources;
	private readonly Func<string, string> _credentialsPathFor;

	/// <summary>
	/// Creates a connector over <paramref name="sources"/> (each with its own <see cref="HttpClient"/>) and
	/// <paramref name="credentialsPathFor"/> (source id → its token file).
	/// </summary>
	public SourceConnector(IReadOnlyList<ISource> sources, Func<string, string> credentialsPathFor) {
		ArgumentNullException.ThrowIfNull(sources);
		ArgumentNullException.ThrowIfNull(credentialsPathFor);
		_sources = sources;
		_credentialsPathFor = credentialsPathFor;
	}

	/// <summary>The real connector: one shared <see cref="HttpClient"/>, the Notion source, token files under <c>~/.weavie/sources</c>.</summary>
	public static SourceConnector CreateDefault() {
		var http = new HttpClient();
		return new SourceConnector([new NotionSource(http)], WeaviePaths.SourceCredentialsFile);
	}

	/// <inheritdoc/>
	public string? IdFor(string target) => _sources.FirstOrDefault(s => s.Match(target))?.Id;

	/// <inheritdoc/>
	public bool IsConnected(string target) {
		if (_sources.FirstOrDefault(s => s.Match(target)) is not { } source) {
			return false;
		}

		try {
			return !string.IsNullOrWhiteSpace(ReadToken(source.Id));
		} catch (InvalidOperationException) {
			// A present-but-unreadable/malformed token file: report not-connected so the open resolver routes the user
			// to (re)connect — which overwrites the bad file — rather than throwing out of the synchronous open path.
			return false;
		}
	}

	/// <inheritdoc/>
	public string SetupUrlFor(string sourceId) => Source(sourceId).SetupUrl;

	/// <summary>
	/// Validates the token the user pasted for <paramref name="sourceId"/> and, on success, writes it (owner-only)
	/// and returns the authorized workspace name. A rejected/empty token throws and is never saved.
	/// </summary>
	public async Task<string> SaveTokenAsync(string sourceId, string token, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrWhiteSpace(token);
		var source = Source(sourceId);
		string workspace = await source.ValidateAsync(token.Trim(), ct).ConfigureAwait(false);
		Write(sourceId, token.Trim());
		return workspace;
	}

	/// <summary>
	/// Fetches <paramref name="target"/> via the source that matches it, using its saved token. Throws when no
	/// source matches or the source isn't connected yet (the host turns that into a "connect first" prompt).
	/// </summary>
	public async Task<SourceDoc> FetchAsync(string target, CancellationToken ct = default) {
		var (source, token) = ConnectedSource(target);
		return await source.FetchAsync(target, token, ct).ConfigureAwait(false);
	}

	/// <inheritdoc/>
	public async Task<SourceDoc> UpdateAsync(string target, string oldStr, string newStr, CancellationToken ct = default) {
		var (source, token) = ConnectedSource(target);
		return await source.UpdateAsync(target, token, oldStr, newStr, ct).ConfigureAwait(false);
	}

	// The source claiming `target` plus its saved token; throws when nothing matches or it isn't connected yet
	// (the host turns that into a "connect first" prompt).
	private (ISource Source, string Token) ConnectedSource(string target) {
		var source = _sources.FirstOrDefault(s => s.Match(target))
			?? throw new InvalidOperationException($"No connected source can open '{target}'.");
		string? token = ReadToken(source.Id);
		if (string.IsNullOrWhiteSpace(token)) {
			throw new InvalidOperationException($"Connect {source.Id} first — there's no token for it.");
		}

		return (source, token);
	}

	private ISource Source(string sourceId) =>
		_sources.FirstOrDefault(s => s.Id == sourceId) ?? throw new InvalidOperationException($"No source registered with id '{sourceId}'.");

	private void Write(string sourceId, string token) {
		string path = _credentialsPathFor(sourceId);
		string? directory = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(directory)) {
			SecureFile.CreateDirectory(directory);
		}

		SecureFile.WriteAllText(path, JsonSerializer.Serialize(new Credentials { Token = token }));
	}

	// The access token saved at ~/.weavie/sources/<id>.json, or null when the file is absent/empty.
	private string? ReadToken(string sourceId) {
		string path = _credentialsPathFor(sourceId);
		if (!File.Exists(path)) {
			return null;
		}

		string text;
		try {
			text = File.ReadAllText(path);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			throw new InvalidOperationException($"Couldn't read the {sourceId} token file {path}: {ex.Message}", ex);
		}

		Credentials? credentials;
		try {
			credentials = JsonSerializer.Deserialize<Credentials>(text);
		} catch (JsonException ex) {
			throw new InvalidOperationException($"The {sourceId} token file {path} is malformed: {ex.Message}", ex);
		}

		return credentials?.Token?.Trim();
	}

	private sealed class Credentials {
		[JsonPropertyName("token")]
		public string Token { get; set; } = string.Empty;
	}
}
