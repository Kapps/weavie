using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Weavie.WorktreeServe;

internal sealed record NodeToolchain(string Node, string CorepackScript, string BinDirectory) {
	public static async Task<NodeToolchain> EnsureAsync(
		string sourceRoot,
		string runRoot,
		CancellationToken cancellationToken) {
		string version = (await File.ReadAllTextAsync(
			Path.Combine(sourceRoot, ".node-version"), cancellationToken).ConfigureAwait(false)).Trim();
		if (version.Length == 0 || version.Any(character => !(char.IsAsciiDigit(character) || character == '.'))) {
			throw new InvalidOperationException(".node-version must contain a numeric Node.js release.");
		}

		string platform = Platform();
		string archiveName = $"node-v{version}-{platform}{(OperatingSystem.IsWindows() ? ".zip" : ".tar.gz")}";
		string cacheBase = Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdgCache
			? xdgCache
			: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
		string installRoot = Path.Combine(cacheBase, "weavie", "node", version, platform);
		string node = Path.Combine(installRoot, OperatingSystem.IsWindows() ? "node.exe" : "bin/node");
		string corepack = Path.Combine(installRoot, "lib", "node_modules", "corepack", "dist", "corepack.js");
		if (!OperatingSystem.IsWindows() && File.Exists(node) && File.Exists(corepack)) {
			return new NodeToolchain(node, corepack, Path.GetDirectoryName(node)!);
		}

		if (OperatingSystem.IsWindows()) {
			corepack = Path.Combine(installRoot, "node_modules", "corepack", "dist", "corepack.js");
			if (File.Exists(node) && File.Exists(corepack)) {
				return new NodeToolchain(node, corepack, installRoot);
			}
		}

		Directory.CreateDirectory(Path.GetDirectoryName(installRoot)!);
		string lockPath = $"{installRoot}.lock";
		using var cacheLock = AcquireCacheLock(lockPath, version);
		if (File.Exists(node) && File.Exists(corepack)) {
			return new NodeToolchain(node, corepack, Path.GetDirectoryName(node)!);
		}
		if (Directory.Exists(installRoot)) {
			Directory.Delete(installRoot, recursive: true);
		}

		string baseUrl = $"https://nodejs.org/dist/v{version}";
		string archivePath = Path.Combine(runRoot, archiveName);
		string checksumsPath = Path.Combine(runRoot, "SHASUMS256.txt");
		using var http = new HttpClient();
		await DownloadAsync(http, $"{baseUrl}/{archiveName}", archivePath, cancellationToken).ConfigureAwait(false);
		await DownloadAsync(http, $"{baseUrl}/SHASUMS256.txt", checksumsPath, cancellationToken).ConfigureAwait(false);
		await VerifyAsync(archivePath, checksumsPath, archiveName, cancellationToken).ConfigureAwait(false);

		string extractRoot = Path.Combine(Path.GetDirectoryName(installRoot)!, $".extract-{Guid.NewGuid():N}");
		Directory.CreateDirectory(extractRoot);
		try {
			if (OperatingSystem.IsWindows()) {
				ZipFile.ExtractToDirectory(archivePath, extractRoot);
			} else {
				await using var archive = File.OpenRead(archivePath);
				await using var gzip = new GZipStream(archive, CompressionMode.Decompress);
				TarFile.ExtractToDirectory(gzip, extractRoot, overwriteFiles: false);
			}

			string extracted = Path.Combine(
				extractRoot,
				ArchiveRootName(archiveName));
			Directory.Move(extracted, installRoot);
		} finally {
			if (Directory.Exists(extractRoot)) {
				Directory.Delete(extractRoot, recursive: true);
			}
		}
		return new NodeToolchain(node, corepack, OperatingSystem.IsWindows() ? installRoot : Path.GetDirectoryName(node)!);
	}

	public IReadOnlyDictionary<string, string> ProcessEnvironment() {
		string currentPath = System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
		return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
			["PATH"] = $"{BinDirectory}{Path.PathSeparator}{currentPath}",
		};
	}

	internal static string ArchiveRootName(string archiveName) {
		if (archiveName.EndsWith(".tar.gz", StringComparison.Ordinal)) {
			return archiveName[..^".tar.gz".Length];
		}
		if (archiveName.EndsWith(".zip", StringComparison.Ordinal)) {
			return archiveName[..^".zip".Length];
		}
		throw new InvalidOperationException($"unsupported Node.js archive: {archiveName}");
	}

	private static FileStream AcquireCacheLock(string path, string version) {
		try {
			return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
		} catch (IOException ex) {
			throw new InvalidOperationException($"another preview is installing Node.js {version}; run again after it finishes.", ex);
		}
	}

	private static async Task DownloadAsync(
		HttpClient http,
		string url,
		string destination,
		CancellationToken cancellationToken) {
		Console.WriteLine($"[worktree-serve] download {url}");
		await using var source = await http.GetStreamAsync(url, cancellationToken).ConfigureAwait(false);
		await using var target = File.Create(destination);
		await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
	}

	private static async Task VerifyAsync(
		string archivePath,
		string checksumsPath,
		string archiveName,
		CancellationToken cancellationToken) {
		string checksums = await File.ReadAllTextAsync(checksumsPath, cancellationToken).ConfigureAwait(false);
		string suffix = $"  {archiveName}";
		string? expected = checksums.Split('\n').FirstOrDefault(line => line.EndsWith(suffix, StringComparison.Ordinal))?
			[..^suffix.Length];
		if (string.IsNullOrEmpty(expected)) {
			throw new InvalidOperationException($"Node.js checksum manifest did not contain {archiveName}.");
		}

		await using var archive = File.OpenRead(archivePath);
		byte[] digest = await SHA256.HashDataAsync(archive, cancellationToken).ConfigureAwait(false);
		string actual = Convert.ToHexStringLower(digest);
		if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) {
			throw new InvalidOperationException($"Node.js archive checksum mismatch for {archiveName}.");
		}
	}

	private static string Platform() {
		string os = OperatingSystem.IsLinux()
			? "linux"
			: OperatingSystem.IsMacOS()
				? "darwin"
				: OperatingSystem.IsWindows()
					? "win"
					: throw new PlatformNotSupportedException("Node.js bootstrap supports Linux, macOS, and Windows.");
		string architecture = RuntimeInformation.OSArchitecture switch {
			Architecture.X64 => "x64",
			Architecture.Arm64 => "arm64",
			_ => throw new PlatformNotSupportedException("Node.js bootstrap supports x64 and arm64."),
		};
		return $"{os}-{architecture}";
	}
}
