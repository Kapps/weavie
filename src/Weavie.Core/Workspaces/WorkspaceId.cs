using System.Security.Cryptography;
using System.Text;

namespace Weavie.Core.Workspaces;

/// <summary>
/// Stable identity for a workspace, derived from its normalized absolute root path (case-insensitive on
/// Windows, trailing separators trimmed), so <c>C:\Foo</c>, <c>c:\foo\</c>, and <c>C:/Foo</c> all map to one
/// id. Keys per-workspace on-disk state and dedupes already-open workspaces. The value is a short hex digest,
/// safe to use as a folder name.
/// </summary>
public readonly record struct WorkspaceId(string Value) {
	/// <summary>Derives the id for <paramref name="rootPath"/> by normalizing the path, then hashing it.</summary>
	public static WorkspaceId ForPath(string rootPath) {
		ArgumentException.ThrowIfNullOrEmpty(rootPath);
		string full = Path.GetFullPath(rootPath);
		string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string normalized = trimmed.Length == 0 ? full : trimmed;
		// The filesystem is case-insensitive on Windows, case-sensitive elsewhere; fold case to match so the
		// same folder reached two ways yields one id (and one per-workspace state folder).
		string key = OperatingSystem.IsWindows() ? normalized.ToLowerInvariant() : normalized;
		string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
		return new WorkspaceId(digest[..16].ToLowerInvariant());
	}
}
