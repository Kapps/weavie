namespace Weavie.Linux.Hosting;

internal sealed record PortalShortcut(string Id, string Description, string Trigger);

internal sealed record PortalActivation(string SessionHandle, string ShortcutId, string? ActivationToken);

internal sealed record PortalBinding(string SessionHandle, IReadOnlySet<string> ShortcutIds);

internal interface IGlobalShortcutsPortal : IDisposable {
	event Action<PortalActivation>? Activated;
	event Action? Invalidated;
	event Action<string>? Log;

	Task<PortalBinding> BindAsync(IReadOnlyList<PortalShortcut> shortcuts, CancellationToken ct);

	Task CloseSessionAsync(string sessionHandle);
}
