using Weavie.Core.FileSystem;

namespace Weavie.Core.FileActivity;

/// <summary>Admits completed file activity into its owning session's ordered stream.</summary>
public interface IFileActivitySink {
	/// <summary>Reports a completed host-backed buffer save.</summary>
	FileActivityTicket ReportBufferSaved(string path, FileStat revision);

	/// <summary>Reports a path whose completed file state is now known.</summary>
	FileActivityTicket ReportChanged(string path, FileStat revision);

	/// <summary>Reports a path whose completed state is deletion.</summary>
	FileActivityTicket ReportDeleted(string path);
}

/// <summary>A non-null sink for tests or hosts that intentionally consume no file activity.</summary>
public sealed class NoopFileActivitySink : IFileActivitySink {
	/// <summary>The shared no-op sink.</summary>
	public static NoopFileActivitySink Instance { get; } = new();

	private NoopFileActivitySink() { }

	/// <inheritdoc/>
	public FileActivityTicket ReportBufferSaved(string path, FileStat revision) => Completed();

	/// <inheritdoc/>
	public FileActivityTicket ReportChanged(string path, FileStat revision) => Completed();

	/// <inheritdoc/>
	public FileActivityTicket ReportDeleted(string path) => Completed();

	private static FileActivityTicket Completed() => new(0, Task.CompletedTask);
}
