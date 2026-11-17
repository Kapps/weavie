using Weavie.Core.Shell;

namespace Weavie.Linux.Hosting;

/// <summary>Reads GTK's <c>gtk-decoration-layout</c> (e.g. <c>menu:minimize,maximize,close</c>) as the page's window buttons.</summary>
internal static class DecorationLayout {
	/// <summary>
	/// Splits at the first <c>:</c> into the buttons before and after the title; keeps minimize, maximize and close
	/// in their order and drops every other token (icon, menu, appmenu, spacers).
	/// </summary>
	public static WindowControls Parse(string? layout) {
		if (string.IsNullOrEmpty(layout)) {
			return WindowControls.None;
		}

		int colon = layout.IndexOf(':', StringComparison.Ordinal);
		return colon < 0
			? new WindowControls(Side(layout), [])
			: new WindowControls(Side(layout[..colon]), Side(layout[(colon + 1)..]));
	}

	private static WindowControl[] Side(string side) => [
		.. side.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
			.Select(Control)
			.OfType<WindowControl>(),
	];

	private static WindowControl? Control(string token) => token switch {
		"minimize" => WindowControl.Minimize,
		"maximize" => WindowControl.MaximizeToggle,
		"close" => WindowControl.Close,
		_ => null,
	};
}
