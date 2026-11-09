using System.Text;
using Weavie.Core.Git;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Proves one batched read returns each file exactly as committed, in order, with absence preserved.</summary>
public sealed class GitReadFilesAtRefsIntegrationTests : IDisposable {
	private readonly TempGitRepo _repo = new("weavie-git-batch");

	[Fact]
	public async Task ReadFilesAtRefs_ReturnsEachBlobInOrderAndAbsenceForEverythingElse() {
		_repo.Write("ascii.txt", "first\nsecond\n");
		_repo.Write("dir name/unicode é.txt", "naïve ✓ 日本\n");
		_repo.Write("empty.txt", string.Empty);
		_repo.Write("no-newline.txt", "tail without newline");
		File.WriteAllBytes(Path.Combine(_repo.Path, "bom.txt"), [.. Encoding.UTF8.GetPreamble(), .. "bom body\n"u8]);
		string first = _repo.Commit("first");
		_repo.Write("ascii.txt", "changed\n");
		string second = _repo.Commit("second");

		var files = await new GitService().ReadFilesAtRefsAsync(_repo.Path, [
			(second, "ascii.txt"),
			(first, "ascii.txt"),
			(first, "dir name/unicode é.txt"),
			(first, "missing file.txt"),
			(first, "empty.txt"),
			(first, "dir name"),
			(first, "no-newline.txt"),
			(first, "bom.txt"),
		]);

		Assert.Equal([
			new GitFileSnapshot(true, "changed\n"),
			new GitFileSnapshot(true, "first\nsecond\n"),
			new GitFileSnapshot(true, "naïve ✓ 日本\n"),
			new GitFileSnapshot(false, string.Empty),
			new GitFileSnapshot(true, string.Empty),
			new GitFileSnapshot(false, string.Empty),
			new GitFileSnapshot(true, "tail without newline"),
			new GitFileSnapshot(true, "bom body\n"),
		], files);
	}

	public void Dispose() => _repo.Dispose();
}
