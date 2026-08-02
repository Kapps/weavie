namespace Weavie.Linux.Hosting;

internal interface ILinuxDesktopAppScope {
	Task EnsureAsync(CancellationToken ct);
}
