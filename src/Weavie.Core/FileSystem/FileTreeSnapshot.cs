namespace Weavie.Core.FileSystem;

/// <summary>Materializes independent file-tree copies without following or creating filesystem links.</summary>
public static class FileTreeSnapshot {
	/// <summary>Mirrors one regular file atomically, rejecting filesystem links in either endpoint.</summary>
	public static void MirrorFile(string source, string destination, string destinationBoundary) {
		ArgumentException.ThrowIfNullOrEmpty(source);
		ArgumentException.ThrowIfNullOrEmpty(destination);
		ArgumentException.ThrowIfNullOrEmpty(destinationBoundary);
		string fullSource = Path.GetFullPath(source);
		string fullDestination = Path.GetFullPath(destination);
		string fullBoundary = Path.GetFullPath(destinationBoundary);
		if (!PathBoundary.Contains(fullBoundary, fullDestination, PathIdentity.Comparison)) {
			throw new InvalidOperationException($"Snapshot destination escapes its boundary: {fullDestination}");
		}
		EnsureUnlinkedDestinationPath(fullBoundary, Path.GetDirectoryName(fullDestination)!);
		var sourceInfo = new FileInfo(fullSource);
		if (sourceInfo.Exists && IsLink(sourceInfo)) {
			throw new InvalidOperationException($"Snapshot source is a filesystem link: {fullSource}");
		}
		if (sourceInfo.LinkTarget is not null) {
			throw new InvalidOperationException($"Snapshot source is a filesystem link: {fullSource}");
		}
		if (Directory.Exists(fullSource)) {
			throw new InvalidOperationException($"Snapshot file source is a directory: {fullSource}");
		}
		var destinationInfo = new FileInfo(fullDestination);
		if (destinationInfo.LinkTarget is not null) {
			throw new InvalidOperationException($"Snapshot destination is a filesystem link: {fullDestination}");
		}
		if (!sourceInfo.Exists) {
			File.Delete(fullDestination);
			return;
		}
		new LocalFileSystem().WriteAllBytes(fullDestination, File.ReadAllBytes(fullSource));
		CopyMode(fullSource, fullDestination);
	}

	/// <summary>
	/// Replaces <paramref name="destination"/> with a regular-file copy of <paramref name="source"/>. A missing
	/// source removes the destination. Every destination component below <paramref name="destinationBoundary"/>
	/// must be a real directory.
	/// </summary>
	public static void MirrorDirectory(string source, string destination, string destinationBoundary) {
		ArgumentException.ThrowIfNullOrEmpty(source);
		ArgumentException.ThrowIfNullOrEmpty(destination);
		ArgumentException.ThrowIfNullOrEmpty(destinationBoundary);
		string fullSource = Path.GetFullPath(source);
		string fullDestination = Path.GetFullPath(destination);
		string fullBoundary = Path.GetFullPath(destinationBoundary);
		if (!PathBoundary.Contains(fullBoundary, fullDestination, PathIdentity.Comparison)) {
			throw new InvalidOperationException($"Snapshot destination escapes its boundary: {fullDestination}");
		}

		EnsureUnlinkedDestinationPath(fullBoundary, Path.GetDirectoryName(fullDestination)!);
		string staging = $"{fullDestination}.snapshot-{Guid.NewGuid():N}";
		try {
			var sourceInfo = new DirectoryInfo(fullSource);
			if (sourceInfo.LinkTarget is not null) {
				throw new InvalidOperationException($"Snapshot source is a filesystem link: {fullSource}");
			}
			if (File.Exists(fullSource)) {
				throw new InvalidOperationException($"Snapshot directory source is a file: {fullSource}");
			}
			if (sourceInfo.Exists) {
				CopyDirectory(sourceInfo, new DirectoryInfo(staging));
			}
			DeleteNoFollow(new DirectoryInfo(fullDestination));
			if (Directory.Exists(staging)) {
				Directory.Move(staging, fullDestination);
			}
		} finally {
			DeleteNoFollow(new DirectoryInfo(staging));
		}
	}

	private static void EnsureUnlinkedDestinationPath(string boundary, string directory) {
		var boundaryInfo = new DirectoryInfo(boundary);
		if (boundaryInfo.LinkTarget is not null) {
			throw new InvalidOperationException($"Snapshot destination boundary is a filesystem link: {boundary}");
		}
		string relative = Path.GetRelativePath(boundary, directory);
		string current = boundary;
		foreach (string segment in relative.Split(
			Path.DirectorySeparatorChar,
			StringSplitOptions.RemoveEmptyEntries)) {
			current = Path.Combine(current, segment);
			var info = new DirectoryInfo(current);
			if (info.Exists && IsLink(info)) {
				throw new InvalidOperationException($"Snapshot destination contains a filesystem link: {current}");
			}
		}
		Directory.CreateDirectory(directory);
	}

	private static void CopyDirectory(DirectoryInfo source, DirectoryInfo destination) {
		if (!source.Exists) {
			throw new DirectoryNotFoundException($"Snapshot source does not exist: {source.FullName}");
		}
		if (IsLink(source)) {
			throw new InvalidOperationException($"Snapshot source contains a filesystem link: {source.FullName}");
		}
		destination.Create();
		CopyMode(source.FullName, destination.FullName);
		foreach (var entry in source.EnumerateFileSystemInfos()) {
			if (IsLink(entry)) {
				throw new InvalidOperationException($"Snapshot source contains a filesystem link: {entry.FullName}");
			}
			string target = Path.Combine(destination.FullName, entry.Name);
			if (entry is DirectoryInfo childDirectory) {
				CopyDirectory(childDirectory, new DirectoryInfo(target));
			} else {
				File.Copy(entry.FullName, target, overwrite: false);
				CopyMode(entry.FullName, target);
			}
		}
	}

	private static void CopyMode(string source, string destination) {
		if (!OperatingSystem.IsWindows()) {
			File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
		}
	}

	private static void DeleteNoFollow(FileSystemInfo entry) {
		entry.Refresh();
		if (!entry.Exists && entry.LinkTarget is null) {
			return;
		}
		if (IsLink(entry)) {
			entry.Delete();
			return;
		}
		if (entry is DirectoryInfo directory) {
			foreach (var child in directory.EnumerateFileSystemInfos()) {
				DeleteNoFollow(child);
			}
			directory.Delete();
			return;
		}
		entry.Delete();
	}

	private static bool IsLink(FileSystemInfo entry) =>
		entry.LinkTarget is not null || entry.Exists && entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
}
