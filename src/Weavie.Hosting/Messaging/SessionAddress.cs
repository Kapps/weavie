namespace Weavie.Hosting.Messaging;

internal sealed record SessionAddress {
	public SessionAddress(string slot, string incarnation) {
		ArgumentException.ThrowIfNullOrEmpty(slot);
		ArgumentException.ThrowIfNullOrEmpty(incarnation);
		Slot = slot;
		Incarnation = incarnation;
	}

	public string Slot { get; }

	public string Incarnation { get; }
}
