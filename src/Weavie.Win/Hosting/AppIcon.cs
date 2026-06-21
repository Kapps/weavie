namespace Weavie.Win.Hosting;

/// <summary>
/// The app/window icon, loaded once from the embedded <c>weavie.ico</c> and shared by every window for its
/// taskbar / Alt-Tab / title-bar icon. The same <c>.ico</c> is the exe icon via csproj <c>&lt;ApplicationIcon&gt;</c>.
/// </summary>
internal static class AppIcon {
	private static readonly Lazy<Icon?> LazyIcon = new(Load);

	/// <summary>The shared app icon, or <c>null</c> if the embedded resource is missing. Do not dispose (shared).</summary>
	public static Icon? Shared => LazyIcon.Value;

	private static Icon? Load() {
		using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("weavie.ico");
		return stream is null ? null : new Icon(stream);
	}
}
