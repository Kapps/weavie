using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Git;

/// <summary>Exact Git-owned state whose removal would lose information from one worktree.</summary>
public sealed record WorktreeDeletionSnapshot(
	string? Branch,
	string HeadCommit,
	WorktreeChangeStatus Changes,
	string Fingerprint);

public sealed partial class GitService {
	/// <summary>
	/// Reads the current ref, index, tracked working tree, and every Git-reported untracked file into one exact
	/// deletion fingerprint. It follows Git's ignore rules and never enumerates the workspace independently.
	/// </summary>
	public async Task<WorktreeDeletionSnapshot> GetDeletionSnapshotAsync(
		string worktreeDirectory,
		CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(worktreeDirectory);
		string root = Path.GetFullPath(worktreeDirectory);
		string? branch = await GetCurrentBranchAsync(root, ct).ConfigureAwait(false);
		string head = await GetHeadCommitAsync(root, ct).ConfigureAwait(false);
		var statusResult = await RunCheckedAsync(root, PorcelainStatusZArgs, ct).ConfigureAwait(false);
		var changes = ParseChangeState(statusResult.StdOut);
		var staged = await RunCheckedAsync(
			root,
			["--no-optional-locks", "diff", "--binary", "--full-index", "--cached", "HEAD", "--"],
			ct).ConfigureAwait(false);
		var unstaged = await RunCheckedAsync(
			root,
			["--no-optional-locks", "diff", "--binary", "--full-index", "--"],
			ct).ConfigureAwait(false);

		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		Append(hash, branch ?? "<detached>");
		Append(hash, head);
		Append(hash, staged.StdOut);
		Append(hash, unstaged.StdOut);
		foreach (string path in changes.UntrackedFiles.Order(StringComparer.Ordinal)) {
			ct.ThrowIfCancellationRequested();
			Append(hash, path);
			AppendUntracked(hash, root, path);
		}

		return new WorktreeDeletionSnapshot(
			branch,
			head,
			changes,
			Convert.ToHexString(hash.GetHashAndReset()));
	}

	private static void AppendUntracked(IncrementalHash hash, string root, string relativePath) {
		string full = Path.GetFullPath(relativePath, root);
		var comparison = OperatingSystem.IsWindows()
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;
		if (!PathBoundary.Contains(root, full, comparison)) {
			throw new InvalidDataException($"Git reported an untracked path outside its worktree: {relativePath}");
		}

		var info = new FileInfo(full);
		info.Refresh();
		if (info.LinkTarget is { } linkTarget) {
			Append(hash, "link");
			Append(hash, linkTarget);
			return;
		}
		if (!info.Exists) {
			throw new FileNotFoundException($"Git-reported untracked file disappeared: {relativePath}", full);
		}

		Append(hash, "file");
		Span<byte> length = stackalloc byte[sizeof(long)];
		BinaryPrimitives.WriteInt64LittleEndian(length, info.Length);
		hash.AppendData(length);
		using var stream = File.OpenRead(full);
		byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
		try {
			long readTotal = 0;
			int read;
			while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) {
				hash.AppendData(buffer, 0, read);
				readTotal += read;
			}
			if (readTotal != info.Length) {
				throw new IOException($"Git-reported untracked file changed while it was inspected: {relativePath}");
			}
		} finally {
			ArrayPool<byte>.Shared.Return(buffer);
		}
	}

	private static void Append(IncrementalHash hash, string value) {
		byte[] bytes = Encoding.UTF8.GetBytes(value);
		Span<byte> length = stackalloc byte[sizeof(int)];
		BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
		hash.AppendData(length);
		hash.AppendData(bytes);
	}
}
