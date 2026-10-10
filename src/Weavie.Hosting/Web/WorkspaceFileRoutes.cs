using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.StaticFiles;

namespace Weavie.Hosting.Web;

/// <summary>Thread-safe, exact-session routing for streamed workspace media and HTML preview assets.</summary>
public sealed class WorkspaceFileRoutes {
	/// <summary>The URL segment under which granted HTML previews resolve their relative assets.</summary>
	public const string PreviewPrefix = "/weavie-preview/";

	private static readonly FileExtensionContentTypeProvider ContentTypes = new();
	private readonly ConcurrentDictionary<string, string> _sessions = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, string> _previewGrants = new(StringComparer.Ordinal);

	/// <summary>Opens file serving for a loaded session rooted at its workspace.</summary>
	public void Register(string sessionId, string workspaceRoot) {
		ArgumentException.ThrowIfNullOrEmpty(sessionId);
		ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
		if (!_sessions.TryAdd(sessionId, Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot)))) {
			throw new InvalidOperationException($"File route '{sessionId}' is already registered.");
		}
	}

	/// <summary>Rejects new media and preview requests for an unloaded session.</summary>
	public void Unregister(string sessionId) {
		_sessions.TryRemove(sessionId, out _);
		foreach (var grant in _previewGrants.Where(entry => entry.Value == sessionId)) {
			_previewGrants.TryRemove(grant);
		}
	}

	/// <summary>Opens a file for asynchronous range streaming, or returns null without revealing why.</summary>
	public MediaResource? Open(string sessionId, string path) {
		if (!_sessions.ContainsKey(sessionId)) {
			return null;
		}

		try {
			string fullPath = Path.GetFullPath(path);
			return ContentTypes.TryGetContentType(fullPath, out string? contentType) && IsPassiveMedia(contentType)
				? OpenFile(fullPath, contentType)
				: null;
		} catch (Exception ex) when (IsFileFailure(ex)) {
			return null;
		}
	}

	/// <summary>
	/// Grants the sandboxed preview of <paramref name="htmlPath"/> read access to its session's workspace, returning
	/// the grant and the root-relative base URL of the file's folder. Throws when the file lies outside that workspace.
	/// </summary>
	public (string Grant, string Base) GrantPreview(string sessionId, string htmlPath) {
		if (!_sessions.TryGetValue(sessionId, out string? root)) {
			throw new InvalidOperationException("This session is no longer loaded.");
		}

		string relative = Path.GetRelativePath(root, Path.GetDirectoryName(Path.GetFullPath(htmlPath))!);
		if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
			|| Path.IsPathRooted(relative)) {
			throw new InvalidOperationException($"{Path.GetFileName(htmlPath)} is outside this session's workspace, so its preview can't load assets.");
		}

		string grant = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
		_previewGrants[grant] = sessionId;
		string folder = relative == "."
			? string.Empty
			: string.Concat(relative.Split(Path.DirectorySeparatorChar).Select(segment => Uri.EscapeDataString(segment) + "/"));
		return (grant, $"{PreviewPrefix}{grant}/{folder}");
	}

	/// <summary>Revokes a preview grant once its pane closes.</summary>
	public void ReleasePreview(string grant) => _previewGrants.TryRemove(grant, out _);

	/// <summary>
	/// Opens a workspace file for a granted preview, or returns null without revealing why. Dot-prefixed segments
	/// (.git, .env) and symlinks are refused so a page can only read plain files inside the workspace.
	/// </summary>
	public MediaResource? OpenPreviewAsset(string grant, string relativePath) {
		if (!_previewGrants.TryGetValue(grant, out string? sessionId)
			|| !_sessions.TryGetValue(sessionId, out string? root)) {
			return null;
		}

		string[] segments = relativePath.Split('/');
		if (segments.Any(segment => segment.Length == 0 || segment.StartsWith('.') || segment.Contains('\\'))) {
			return null;
		}

		try {
			string path = root;
			foreach (string segment in segments) {
				path = Path.Combine(path, segment);
				if (new FileInfo(path).LinkTarget is not null) {
					return null;
				}
			}

			return OpenFile(
				path,
				ContentTypes.TryGetContentType(path, out string? contentType) ? contentType : "application/octet-stream");
		} catch (Exception ex) when (IsFileFailure(ex)) {
			return null;
		}
	}

	private static MediaResource? OpenFile(string fullPath, string contentType) {
		var stream = new FileStream(
			fullPath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.ReadWrite | FileShare.Delete,
			64 * 1024,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		return new MediaResource(stream, contentType, new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath)), stream.Length);
	}

	private static bool IsFileFailure(Exception ex) =>
		ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

	private static bool IsPassiveMedia(string contentType) =>
		contentType.StartsWith("video/", StringComparison.Ordinal)
		|| contentType.StartsWith("image/", StringComparison.Ordinal)
			&& contentType != "image/svg+xml";
}

/// <summary>An already-open media stream and the metadata used for HTTP validators and ranges.</summary>
public sealed record MediaResource(Stream Stream, string ContentType, DateTimeOffset LastModified, long Length);
