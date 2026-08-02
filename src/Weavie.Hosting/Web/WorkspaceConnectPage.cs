namespace Weavie.Hosting.Web;

/// <summary>The unauthenticated workspace entry rendered directly at <c>/index.html</c>.</summary>
internal static class WorkspaceConnectPage {
	public static Task WriteAsync(Microsoft.AspNetCore.Http.HttpContext context, bool invalidToken) =>
		TokenConnectPage.WriteAsync(
			context,
			"Enter the workspace token. This browser will remember it securely for future visits.",
			"Workspace token",
			"/index.html",
			"/weavie.png",
			invalidToken);
}
