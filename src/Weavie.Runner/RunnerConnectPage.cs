using Microsoft.AspNetCore.Http;
using Weavie.Hosting.Web;

namespace Weavie.Runner;

/// <summary>The runner-token entry rendered at the canonical browser URL.</summary>
internal static class RunnerConnectPage {
	public static Task WriteAsync(HttpContext context, bool invalidToken) =>
		TokenConnectPage.WriteAsync(
			context,
			"Enter the runner token. This browser will remember it securely for future visits.",
			"Runner token",
			"/",
			string.Empty,
			invalidToken);
}
