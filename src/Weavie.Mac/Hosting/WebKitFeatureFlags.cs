using System.Runtime.InteropServices;
using Foundation;
using ObjCRuntime;
using WebKit;

namespace Weavie.Mac.Hosting;

/// <summary>
/// Toggles WebKit feature flags on a <see cref="WKPreferences"/> via private SPI (the surface Safari's "Feature
/// Flags" pane exposes), to turn off "Prefer 60fps" so WKWebView renders at the display's full refresh (120Hz).
/// Flags come from <c>+[WKPreferences _features]</c> (array of <c>_WKFeature</c>); the setter is
/// <c>-[WKPreferences _setEnabled:forFeature:]</c>.
/// PRIVATE API, NOT App Store safe: guarded by <c>respondsToSelector:</c> so a dropped/renamed SPI no-ops to 60Hz.
/// </summary>
internal static partial class WebKitFeatureFlags {
	private const string Prefer60FpsKey = "PreferPageRenderingUpdatesNear60FPSEnabled";

	[LibraryImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
	private static partial IntPtr SendForPtr(IntPtr receiver, IntPtr selector);

	[LibraryImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
	[return: MarshalAs(UnmanagedType.U1)]
	private static partial bool RespondsTo(IntPtr receiver, IntPtr selector, IntPtr arg);

	[LibraryImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
	private static partial void SetEnabledForFeature(
		IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.U1)] bool enabled, IntPtr feature);

	/// <summary>
	/// Turns off the "prefer 60fps" rendering pace so the page can update at the display's full refresh rate.
	/// No-ops (with a log line) if the SPI or flag is unavailable.
	/// </summary>
	public static void DisablePrefer60Fps(WKPreferences preferences) {
		ArgumentNullException.ThrowIfNull(preferences);

		// _features is a class property on WKPreferences, so query the class object, not the instance.
		var prefsClass = Class.GetHandle("WKPreferences");
		IntPtr featuresSelector = Selector.GetHandle("_features");
		IntPtr respondsSelector = Selector.GetHandle("respondsToSelector:");
		if (prefsClass == IntPtr.Zero || !RespondsTo(prefsClass, respondsSelector, featuresSelector)) {
			Console.WriteLine("[weavie] +[WKPreferences _features] SPI absent — staying at 60Hz");
			return;
		}

		var features = Runtime.GetNSObject<NSArray>(SendForPtr(prefsClass, featuresSelector));
		if (features is null) {
			return;
		}

		IntPtr keySelector = Selector.GetHandle("key");
		IntPtr setEnabledSelector = Selector.GetHandle("_setEnabled:forFeature:");
		var candidates = new List<string>();
		for (nuint i = 0; i < features.Count; i++) {
			var featureHandle = features.ValueAt(i);
			string key = NSString.FromHandle(SendForPtr(featureHandle, keySelector)) ?? string.Empty;
			if (key == Prefer60FpsKey) {
				SetEnabledForFeature(preferences.Handle, setEnabledSelector, enabled: false, featureHandle);
				Console.WriteLine($"[weavie] disabled {Prefer60FpsKey} — targeting full-refresh (120Hz)");
				return;
			}

			if (key.Contains("60", StringComparison.OrdinalIgnoreCase)
				|| key.Contains("Render", StringComparison.OrdinalIgnoreCase)
				|| key.Contains("Prefer", StringComparison.OrdinalIgnoreCase)) {
				candidates.Add(key);
			}
		}

		Console.WriteLine(
			$"[weavie] {Prefer60FpsKey} not found among {features.Count} features. " +
			$"Candidates: {(candidates.Count > 0 ? string.Join(", ", candidates) : "(none)")}");
	}
}
