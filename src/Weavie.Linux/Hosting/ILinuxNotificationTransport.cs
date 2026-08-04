namespace Weavie.Linux.Hosting;

internal sealed record LinuxNotificationRequest(
	uint ReplacesId,
	string AppName,
	string AppIcon,
	string DesktopEntry,
	string Title,
	string Body,
	IReadOnlyList<string> Actions,
	bool SuppressSound,
	int ExpireTimeout);

internal sealed record LinuxNotificationActivation(uint Id, string Action, string? ActivationToken);

internal interface ILinuxNotificationTransport : IDisposable {
	event Action<LinuxNotificationActivation>? Activated;
	event Action<uint>? Closed;
	event Action? Invalidated;

	Task<bool> IsAvailableAsync(CancellationToken ct);

	Task<uint> ShowAsync(LinuxNotificationRequest notification, CancellationToken ct);

	Task CloseAsync(uint id, CancellationToken ct);
}
