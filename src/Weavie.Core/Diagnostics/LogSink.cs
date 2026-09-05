using System.Text;

namespace Weavie.Core.Diagnostics;

internal interface ILogSink {
	string Path { get; }
	string Failure { get; }
	void Append(string line);
}

internal sealed class NoopLogSink : ILogSink {
	public static NoopLogSink Instance { get; } = new();
	public string Path => string.Empty;
	public string Failure => string.Empty;
	public void Append(string line) { }
}

internal sealed class FileLogSink : ILogSink, IDisposable {
	private readonly StreamWriter? _writer;

	public FileLogSink(string path) {
		Path = path;
		try {
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
			var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read };
			if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
			_writer = new StreamWriter(new FileStream(path, options), new UTF8Encoding(false)) { AutoFlush = true };
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Failure = ex.Message;
		}
	}

	public string Path { get; }
	public string Failure { get; private set; } = string.Empty;

	public void Append(string line) {
		try {
			_writer?.WriteLine($"{DateTimeOffset.UtcNow:o} {line}");
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Failure = ex.Message;
		}
	}

	public void Dispose() => _writer?.Dispose();
}
