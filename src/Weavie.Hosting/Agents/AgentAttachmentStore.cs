using Weavie.Core.Agents;
using Weavie.Core.Editor;

namespace Weavie.Hosting.Agents;

/// <summary>Stages structured-agent images by client id until an exact submission claims them.</summary>
internal sealed class AgentAttachmentStore(PastedImageStore images) {
	private readonly Dictionary<string, AgentInputAttachment> _items = new(StringComparer.Ordinal);
	private readonly Dictionary<string, IReadOnlyList<string>> _submissionReceipts = new(StringComparer.Ordinal);
	private readonly HashSet<string> _closedAttachmentIds = new(StringComparer.Ordinal);
	private readonly Lock _gate = new();

	public AgentInputAttachment? Add(string id, string mime, string extension, byte[] bytes) {
		ArgumentException.ThrowIfNullOrEmpty(id);
		ArgumentException.ThrowIfNullOrEmpty(mime);
		ArgumentException.ThrowIfNullOrEmpty(extension);
		ArgumentNullException.ThrowIfNull(bytes);
		lock (_gate) {
			if (_closedAttachmentIds.Contains(id)) {
				return null;
			}
			if (_items.TryGetValue(id, out var existing)) {
				if (!string.Equals(existing.Mime, mime, StringComparison.Ordinal)) {
					throw new InvalidOperationException($"Attachment '{id}' was replayed with a different image type.");
				}
				return existing;
			}

			var attachment = new AgentInputAttachment {
				Id = id,
				Mime = mime,
				Path = images.Write(extension, bytes),
			};
			_items.Add(id, attachment);
			return attachment;
		}
	}

	public IReadOnlyList<AgentInputAttachment> Resolve(IReadOnlyList<string> ids) {
		ArgumentNullException.ThrowIfNull(ids);
		lock (_gate) {
			var seen = new HashSet<string>(StringComparer.Ordinal);
			var resolved = new List<AgentInputAttachment>(ids.Count);
			foreach (string id in ids) {
				if (!seen.Add(id)) {
					throw new InvalidOperationException($"Attachment '{id}' was submitted more than once.");
				}
				if (!_items.TryGetValue(id, out var item)) {
					throw new InvalidOperationException($"Attachment '{id}' is not ready for this session.");
				}
				resolved.Add(item);
			}
			return resolved;
		}
	}

	public void CommitSubmission(string submissionId, IReadOnlyList<string> ids) {
		ArgumentException.ThrowIfNullOrEmpty(submissionId);
		ArgumentNullException.ThrowIfNull(ids);
		lock (_gate) {
			foreach (string id in ids) {
				_items.Remove(id);
				_closedAttachmentIds.Add(id);
			}
			_submissionReceipts[submissionId] = [.. ids];
		}
	}

	public bool TryReceipt(string submissionId, out IReadOnlyList<string> attachmentIds) {
		ArgumentException.ThrowIfNullOrEmpty(submissionId);
		lock (_gate) {
			return _submissionReceipts.TryGetValue(submissionId, out attachmentIds!);
		}
	}

	public void Remove(string id) {
		ArgumentException.ThrowIfNullOrEmpty(id);
		lock (_gate) {
			_closedAttachmentIds.Add(id);
			if (_items.Remove(id, out var attachment)) {
				images.Delete(attachment.Path);
			}
		}
	}
}
