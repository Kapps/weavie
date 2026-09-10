namespace Weavie.Core.FileActivity;

/// <summary>Owns the watch lifetime surrounding a navigation snapshot.</summary>
public interface IWorkspaceNavigationObserver {
	/// <summary>Completes once initial workspace observation is installed.</summary>
	Task ObservationReady { get; }

	/// <summary>Prevents reconciliation from removing watches during navigation discovery.</summary>
	Task<IWorkspaceNavigationObservation> ObserveNavigationAsync(CancellationToken ct);
}

/// <summary>Keeps navigation-discovered directories observed until their inventory seed is complete.</summary>
public interface IWorkspaceNavigationObservation : IDisposable {
	/// <summary>Installs observation before navigation enumerates a directory.</summary>
	void ObserveDirectory(string path);
}
