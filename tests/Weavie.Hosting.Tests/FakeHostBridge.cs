using System.Text.Json;

namespace Weavie.Hosting.Tests;

/// <summary>
/// An in-memory <see cref="IHostBridge"/> for tests: captures every <see cref="PostToWeb"/> message in order
/// and lets a test raise an inbound page message via <see cref="Receive"/> (driving <c>HostCore.OnWebMessage</c>
/// exactly as the real web view would). The shared host components depend only on the bridge contract, so this
/// is all they need to exercise routing end-to-end without a web view.
/// </summary>
internal sealed class FakeHostBridge : IHostBridge {
	private readonly List<string> _posted = [];
	private readonly Lock _gate = new();

	public event Action<string>? MessageReceived;

	/// <summary>Whether a live host is subscribed to inbound page messages.</summary>
	public bool HasMessageReceiver => MessageReceived is not null;

	public void PostToWeb(string json) {
		lock (_gate) {
			_posted.Add(json);
		}
	}

	/// <summary>Every message posted to the page, in order.</summary>
	public IReadOnlyList<string> Posted {
		get {
			lock (_gate) {
				return [.. _posted];
			}
		}
	}

	/// <summary>Posted messages of a given <c>type</c>, parsed.</summary>
	public IReadOnlyList<JsonElement> PostedOfType(string type) {
		var result = new List<JsonElement>();
		foreach (string json in Posted) {
			using var doc = JsonDocument.Parse(json);
			if (doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == type) {
				result.Add(doc.RootElement.Clone());
			}
		}

		return result;
	}

	/// <summary>The last posted message of a given <c>type</c>, or null when none was posted.</summary>
	public JsonElement? LastOfType(string type) {
		var all = PostedOfType(type);
		return all.Count == 0 ? null : all[^1];
	}

	/// <summary>Forgets every captured message (so a test can assert only on what happens next).</summary>
	public void Clear() {
		lock (_gate) {
			_posted.Clear();
		}
	}

	/// <summary>Raises an inbound web message, as the page's bridge would.</summary>
	public void Receive(string json) => MessageReceived?.Invoke(json);
}
