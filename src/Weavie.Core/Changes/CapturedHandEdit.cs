namespace Weavie.Core.Changes;

/// <summary>An action-time correction capture whose downstream publication may complete after the save response.</summary>
public sealed class CapturedHandEdit {
	private Action? _complete;

	internal CapturedHandEdit(Action complete) {
		ArgumentNullException.ThrowIfNull(complete);
		_complete = complete;
	}

	/// <summary>A capture containing no corrections.</summary>
	public static CapturedHandEdit None { get; } = new(static () => { });

	/// <summary>Publishes the captured corrections at most once.</summary>
	public void Complete() => Interlocked.Exchange(ref _complete, null)?.Invoke();
}
