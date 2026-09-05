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
	// Caps this run's log so a long-lived host session can't grow it without bound; trimmed once it passes twice
	// the cap (like Terminal.ScrollbackLog) so a file sitting just over the line isn't rewritten on every append.
	private const long CapBytes = 8 * 1024 * 1024;

	private readonly long _capBytes;
	private readonly FileStream? _stream;
	private readonly StreamWriter? _writer;

	public FileLogSink(string path) : this(path, CapBytes) { }

	/// <summary>Test seam: builds with an arbitrary cap so trimming can be exercised without an 8 MB fixture.</summary>
	internal FileLogSink(string path, long capBytes) {
		Path = path;
		_capBytes = capBytes;
		try {
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
			var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.Read };
			if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
			_stream = new FileStream(path, options);
			_writer = new StreamWriter(_stream, new UTF8Encoding(false)) { AutoFlush = true };
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Failure = ex.Message;
		}
	}

	public string Path { get; }
	public string Failure { get; private set; } = string.Empty;

	public void Append(string line) {
		try {
			_writer?.WriteLine($"{DateTimeOffset.UtcNow:o} {line}");
			TrimIfOversized();
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Failure = ex.Message;
		}
	}

	private void TrimIfOversized() {
		if (_stream is null || _stream.Length <= _capBytes * 2) return;

		byte[] all = new byte[_stream.Length];
		_stream.Seek(0, SeekOrigin.Begin);
		int read = 0;
		while (read < all.Length) {
			int n = _stream.Read(all, read, all.Length - read);
			if (n == 0) break;
			read += n;
		}

		// Keep the last _capBytes, advanced to the next newline so a line isn't split at the top of the file.
		int cut = all.Length - (int)_capBytes;
		int newline = Array.IndexOf(all, (byte)'\n', cut);
		int keepFrom = newline >= 0 ? newline + 1 : cut;

		_stream.Seek(0, SeekOrigin.Begin);
		_stream.Write(all, keepFrom, all.Length - keepFrom);
		_stream.SetLength(all.Length - keepFrom);
		_stream.Seek(0, SeekOrigin.End);
	}

	public void Dispose() => _writer?.Dispose();
}
