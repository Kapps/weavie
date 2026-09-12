using System.Text.Json;

namespace Weavie.Hosting;

/// <summary>
/// Builds the outbound JavaScript a webview host evaluates to deliver a raw JSON message to the page. Shared so
/// every webview bridge (Win / Mac / Linux) frames the <c>this.__weavieDeliver(...)</c> call identically — the
/// JS delivery contract, not anything native. Each host supplies only the eval transport + its UI-thread hop.
/// </summary>
public static class WebBridgeScript {
	/// <summary>The <c>this.__weavieDeliver(...)</c> call delivering <paramref name="json"/> as a JS string literal (trim-safe; no reflection).</summary>
	public static string Receive(string json) {
		ArgumentNullException.ThrowIfNull(json);
		string literal = $"\"{JsonEncodedText.Encode(json)}\"";
		return $"this.__weavieDeliver && this.__weavieDeliver({literal});";
	}
}
