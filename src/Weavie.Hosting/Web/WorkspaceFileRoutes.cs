using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.StaticFiles;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting.Web;

/// <summary>Thread-safe, exact-session routing for streamed workspace media and HTML preview assets.</summary>
public sealed class WorkspaceFileRoutes {
	/// <summary>The URL segment under which granted HTML previews resolve their relative assets.</summary>
	public const string PreviewPrefix = "/weavie-preview/";

	private static readonly FileExtensionContentTypeProvider ContentTypes = new();
	private readonly ConcurrentDictionary<string, string> _sessions = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, PreviewOwner> _previewGrants = new(StringComparer.Ordinal);

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
		foreach (var grant in _previewGrants.Where(entry => entry.Value.Session == sessionId)) {
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
	/// Grants <paramref name="owner"/>'s sandboxed preview of <paramref name="htmlPath"/> read access to its session's
	/// workspace, returning the grant and the root-relative base URL of the file's folder. Throws when that folder is
	/// outside the workspace or hidden, since its assets could never load.
	/// </summary>
	internal (string Grant, string Base) GrantPreview(string sessionId, MessagePeer owner, string htmlPath) {
		if (!_sessions.TryGetValue(sessionId, out string? root)) {
			throw new InvalidOperationException("This session is no longer loaded.");
		}

		string relative = Path.GetRelativePath(root, Path.GetDirectoryName(Path.GetFullPath(htmlPath))!);
		string[] folders = relative == "." ? [] : relative.Split(Path.DirectorySeparatorChar);
		if (Path.IsPathRooted(relative) || folders.Any(folder => folder.StartsWith('.'))) {
			throw new InvalidOperationException(
				$"{Path.GetFileName(htmlPath)} is outside this session's workspace or in a hidden folder, so its preview can't load assets.");
		}

		string grant = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
		_previewGrants[grant] = new PreviewOwner(sessionId, owner);
		if (!_sessions.ContainsKey(sessionId)) {
			_previewGrants.TryRemove(grant, out _);
			throw new InvalidOperationException("This session is no longer loaded.");
		}

		return (grant, $"{PreviewPrefix}{grant}/{string.Concat(folders.Select(folder => Uri.EscapeDataString(folder) + "/"))}");
	}

	/// <summary>Revokes one of <paramref name="owner"/>'s preview grants once its pane closes.</summary>
	internal void ReleasePreview(MessagePeer owner, string grant) {
		if (_previewGrants.TryGetValue(grant, out var held) && held.Owner == owner) {
			_previewGrants.TryRemove(KeyValuePair.Create(grant, held));
		}
	}

	/// <summary>Revokes every preview grant a disconnected page held.</summary>
	internal void ReleasePreviews(MessagePeer owner) {
		foreach (var grant in _previewGrants.Where(entry => entry.Value.Owner == owner)) {
			_previewGrants.TryRemove(grant);
		}
	}

	/// <summary>
	/// Opens a workspace file for a granted preview, or returns null without revealing why. Each segment is matched
	/// against its real on-disk entry, so hidden entries (.git, .env, even via a Windows short name), drive or stream
	/// syntax, and symlinks are all refused and a page can only read plain files inside the workspace.
	/// </summary>
	public MediaResource? OpenPreviewAsset(string grant, string relativePath) {
		if (!_previewGrants.TryGetValue(grant, out var owner)
			|| !_sessions.TryGetValue(owner.Session, out string? root)) {
			return null;
		}

		try {
			string path = root;
			foreach (string segment in relativePath.Split('/')) {
				if (segment.Length == 0 || segment.IndexOfAny(['\\', ':', '*', '?']) >= 0) {
					return null;
				}

				var entries = new DirectoryInfo(path).EnumerateFileSystemInfos(segment).Take(2).ToArray();
				if (entries is not [{ LinkTarget: null } entry] || entry.Name.StartsWith('.')) {
					return null;
				}

				path = entry.FullName;
			}

			return File.Exists(path)
				? OpenFile(path, ContentTypes.TryGetContentType(path, out string? contentType) ? contentType : "application/octet-stream")
				: null;
		} catch (Exception ex) when (IsFileFailure(ex)) {
			return null;
		}
	}

	private static MediaResource OpenFile(string fullPath, string contentType) {
		var stream = new FileStream(
			fullPath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.ReadWrite | FileShare.Delete,
			64 * 1024,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		try {
			return new MediaResource(stream, contentType, new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath)), stream.Length);
		} catch {
			stream.Dispose();
			throw;
		}
	}

	private sealed record PreviewOwner(string Session, MessagePeer Owner);

	private static bool IsFileFailure(Exception ex) =>
		ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

	private static bool IsPassiveMedia(string contentType) =>
		contentType.StartsWith("video/", StringComparison.Ordinal)
		|| contentType.StartsWith("image/", StringComparison.Ordinal)
			&& contentType != "image/svg+xml";
}

/// <summary>An already-open media stream and the metadata used for HTTP validators and ranges.</summary>
public sealed record MediaResource(Stream Stream, string ContentType, DateTimeOffset LastModified, long Length);
