using Weavie.Core.FileSystem;

namespace Weavie.Core.Tests;

internal sealed class UnreadableFileSystem(Exception error) : IFileSystem {
	public bool FileExists(string path) => throw error;
	public bool TryReadAllText(string path, out string contents) => throw error;
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
