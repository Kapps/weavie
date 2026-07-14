using System.Collections.Concurrent;
using Microsoft.AspNetCore.StaticFiles;
using Weavie.Core.Editor;

namespace Weavie.Hosting.Web;

/// <summary>Thread-safe, exact-session routing for streamed workspace media.</summary>
public sealed class WorkspaceMediaRoutes {
	private static readonly FileExtensionContentTypeProvider ContentTypes = new();
	private readonly ConcurrentDictionary<string, WorkspaceFileScope> _sessions = new(StringComparer.Ordinal);

	/// <summary>Registers the exact file roots exposed by a loaded session.</summary>
	public void Register(string sessionId, IEnumerable<string> roots) {
		ArgumentException.ThrowIfNullOrEmpty(sessionId);
		if (!_sessions.TryAdd(sessionId, new WorkspaceFileScope(roots))) {
			throw new InvalidOperationException($"Media route '{sessionId}' is already registered.");
		}
	}

	/// <summary>Rejects new media requests for an unloaded session.</summary>
	public void Unregister(string sessionId) => _sessions.TryRemove(sessionId, out _);

	/// <summary>Opens a confined file for asynchronous range streaming, or returns null without revealing why.</summary>
	public MediaResource? Open(string sessionId, string path) {
		if (!_sessions.TryGetValue(sessionId, out var scope)) {
			return null;
		}

		FileStream? stream = null;
		try {
			if (!scope.Contains(path)) {
				return null;
			}

			string fullPath = Path.GetFullPath(path);
			if (!ContentTypes.TryGetContentType(fullPath, out string? contentType)
				|| !IsPassiveMedia(contentType)) {
				return null;
			}

			stream = new FileStream(
				fullPath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete,
				64 * 1024,
				FileOptions.Asynchronous | FileOptions.SequentialScan);
			return new MediaResource(
				stream,
				contentType,
				new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath)),
				stream.Length);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
			stream?.Dispose();
			return null;
		}
	}

	private static bool IsPassiveMedia(string contentType) =>
		contentType.StartsWith("video/", StringComparison.Ordinal)
		|| contentType.StartsWith("image/", StringComparison.Ordinal)
			&& contentType != "image/svg+xml";
}

/// <summary>An already-open media stream and the metadata used for HTTP validators and ranges.</summary>
public sealed record MediaResource(Stream Stream, string ContentType, DateTimeOffset LastModified, long Length);
