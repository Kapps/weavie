using Weavie.Core.FileActivity;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Tests;

internal sealed class CapturingFileActivitySink : IFileActivitySink {
	private readonly List<FileActivityFact> _facts = [];
	private long _sequence;

	public IReadOnlyList<FileActivityFact> Facts => _facts;

	public FileActivityTicket ReportBufferSaved(string path, FileStat revision) =>
		Add(sequence => new BufferSaved(sequence, path, revision));

	public FileActivityTicket ReportChanged(string path, FileStat revision) =>
		Add(sequence => new FileChanged(sequence, path, revision));

	public FileActivityTicket ReportDeleted(string path) =>
		Add(sequence => new FileDeleted(sequence, path));

	public void Clear() => _facts.Clear();

	private FileActivityTicket Add(Func<long, FileActivityFact> create) {
		long sequence = ++_sequence;
		_facts.Add(create(sequence));
		return new FileActivityTicket(sequence, Task.CompletedTask);
	}
}
