using System.Text;
using Weavie.Core.FileSystem;
using Weavie.Core.Workflows;
using Weavie.Workflows;
using Xunit;

namespace Weavie.Core.Tests.Workflows;

public sealed class WorkflowFileReaderTests {
	[Fact]
	public void ReadsCurrentCompleteTextByContentsIncludingBomAndEmptyFiles() {
		using var dir = new TempDirectory("weavie-evidence-reader");
		var reader = new WorkflowFileReader(new LocalFileSystem());
		string text = new('x', 300_000);
		text += "\nRésumé 世界 🚀 END\n";
		File.WriteAllText(dir.Combine("document.mp4"), text, Encoding.Unicode);
		Assert.Equal(new WorkflowFileRead.Text(text), reader.Read(Files(dir.Path), "./document.mp4"));
		File.WriteAllText(dir.Combine("document.mp4"), "edited", new UTF8Encoding(true));
		Assert.Equal(new WorkflowFileRead.Text("edited"), reader.Read(Files(dir.Path), "document.mp4"));
		File.WriteAllText(dir.Combine("document.mp4"), "");
		Assert.Equal(new WorkflowFileRead.Text(""), reader.Read(Files(dir.Path), "document.mp4"));
		File.Delete(dir.Combine("document.mp4"));
		Assert.Equal(new WorkflowFileRead.Unreadable("file no longer exists"), reader.Read(Files(dir.Path), "document.mp4"));
	}

	[Theory]
	[InlineData(new byte[] { 65, 0, 66 })]
	[InlineData(new byte[] { 0xff, 0xfe, 0xfd, 0xfc, 0x00, 0x00 })]
	[InlineData(new byte[] { 0xc3, 0x28 })]
	public void BinaryContentIsRejectedEvenWithATextExtension(byte[] bytes) {
		var fileSystem = new InMemoryFileSystem();
		string root = Path.GetTempPath();
		fileSystem.WriteAllBytes(Path.Combine(root, "document.txt"), bytes);
		Assert.Equal(new WorkflowFileRead.Unreadable("not readable text"), new WorkflowFileReader(fileSystem).Read(Files(root), "document.txt"));
	}

	[Theory]
	[InlineData("missing", "file no longer exists")]
	[InlineData("directory", "file no longer exists")]
	[InlineData("denied", "access denied")]
	[InlineData("io", "file could not be read")]
	public void ExpectedFailuresReturnSafeReasons(string failure, string expected) {
		var reader = new WorkflowFileReader(new FailingFileSystem(failure));
		Assert.Equal(new WorkflowFileRead.Unreadable(expected), reader.Read(Files(Path.GetTempPath()), "document.txt"));
	}

	[Theory]
	[InlineData("../outside.txt")]
	[InlineData("\u0000")]
	public void InvalidPathsDoNotReachTheFilesystem(string path) =>
		Assert.Equal(new WorkflowFileRead.Unreadable("not a file path inside the worktree or run documents directory"),
			new WorkflowFileReader(new FailingFileSystem("unexpected")).Read(Files(Path.GetTempPath()), path));

	private static WorkflowFileScope Files(string worktree) => new(worktree, Path.Combine(Path.GetTempPath(), "documents"));

	private sealed class FailingFileSystem(string failure) : IFileSystem {
		public bool TryReadAllText(string path, out string contents) => throw failure switch {
			"missing" => new FileNotFoundException("private detail"),
			"directory" => new DirectoryNotFoundException("private detail"),
			"denied" => new UnauthorizedAccessException("private detail"),
			"io" => new IOException("private detail"),
			_ => new InvalidOperationException("Unexpected filesystem access"),
		};
		public bool FileExists(string path) => throw new NotSupportedException();
		public bool DirectoryExists(string path) => throw new NotSupportedException();
		public bool TryGetStat(string path, out FileStat stat) => throw new NotSupportedException();
		public IReadOnlyList<DirectoryEntry> EnumerateDirectory(string path) => throw new NotSupportedException();
		public string ReadAllText(string path) => throw new NotSupportedException();
		public byte[] ReadAllBytes(string path) => throw new NotSupportedException();
		public void WriteAllText(string path, string contents) => throw new NotSupportedException();
		public void WriteAllBytes(string path, byte[] contents) => throw new NotSupportedException();
		public void AppendAllText(string path, string contents) => throw new NotSupportedException();
		public void WriteAllTextAtomic(string path, string contents) => throw new NotSupportedException();
		public void DeleteFile(string path) => throw new NotSupportedException();
	}
}
