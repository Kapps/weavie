namespace Weavie.Hosting.Web;

/// <summary>Names optional worker control-plane capabilities advertised by <c>/control/status</c>.</summary>
public static class WorkspaceControlProtocol {
	/// <summary>The worker exposes message-ingress health at <c>/control/health</c>.</summary>
	public const string MessageHealth = "message-health-v1";
}
