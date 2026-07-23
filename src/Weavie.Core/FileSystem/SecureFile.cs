using System.Text;

namespace Weavie.Core.FileSystem;

/// <summary>
/// Writes files that hold secrets (auth tokens). On POSIX they get owner-only permissions (0600 file, 0700
/// dir) so another local user can't read them under a permissive umask; on Windows the user-profile ACL
/// already restricts the home directory, so the mode calls are no-ops.
/// </summary>
public static class SecureFile {
	private const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
	private const UnixFileMode OwnerDir = OwnerFile | UnixFileMode.UserExecute;

	/// <summary>Creates <paramref name="directory"/> (and parents), restricting it to the owner on POSIX.</summary>
	public static void CreateDirectory(string directory) {
		Directory.CreateDirectory(directory);
		if (!OperatingSystem.IsWindows()) {
			File.SetUnixFileMode(directory, OwnerDir);
		}
	}

	/// <summary>Writes <paramref name="bytes"/> to <paramref name="path"/> owner-only on POSIX.</summary>
	public static void WriteAllBytes(string path, byte[] bytes) {
		if (OperatingSystem.IsWindows()) {
			File.WriteAllBytes(path, bytes);
			return;
		}

		// UnixCreateMode applies only when the file is created; re-assert for the truncate-an-existing-file case,
		// before writing, so the content is never visible at a wider mode.
		using var stream = new FileStream(path, new FileStreamOptions {
			Mode = FileMode.Create,
			Access = FileAccess.Write,
			UnixCreateMode = OwnerFile,
		});
		File.SetUnixFileMode(path, OwnerFile);
		stream.Write(bytes);
	}

	/// <summary>Writes UTF-8 <paramref name="text"/> to <paramref name="path"/> owner-only on POSIX.</summary>
	public static void WriteAllText(string path, string text) => WriteAllBytes(path, Encoding.UTF8.GetBytes(text));

	/// <summary>Atomically replaces a UTF-8 text file without exposing a wider-permission intermediate file.</summary>
	public static void WriteAllTextAtomic(string path, string text) {
		string fullPath = Path.GetFullPath(path);
		string? directory = Path.GetDirectoryName(fullPath);
		if (!string.IsNullOrEmpty(directory)) {
			CreateDirectory(directory);
		}

		string temporary = $"{fullPath}.{Guid.NewGuid():n}.tmp";
		try {
			WriteAllText(temporary, text);
			if (File.Exists(fullPath)) {
				File.Replace(temporary, fullPath, null);
			} else {
				File.Move(temporary, fullPath);
			}
		} finally {
			if (File.Exists(temporary)) {
				File.Delete(temporary);
			}
		}
	}

	/// <summary>Restricts an existing file to the owner on POSIX (no-op on Windows or a missing file).</summary>
	public static void Restrict(string path) {
		if (!OperatingSystem.IsWindows() && File.Exists(path)) {
			File.SetUnixFileMode(path, OwnerFile);
		}
	}
}
