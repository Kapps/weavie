namespace Weavie.Core.Layout;

/// <summary>A screen work-area or window rectangle in screen pixels — toolkit-agnostic placement math.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height) {
	/// <summary>True when this rectangle overlaps <paramref name="other"/> at all.</summary>
	public bool IntersectsWith(PixelRect other) =>
		X < other.X + other.Width && other.X < X + Width
		&& Y < other.Y + other.Height && other.Y < Y + Height;
}

/// <summary>
/// The geometry a host should apply at startup. When <see cref="UseSaved"/> is <c>false</c> the host centers a
/// <see cref="Width"/>×<see cref="Height"/> default window; otherwise it positions to (<see cref="X"/>,
/// <see cref="Y"/>, <see cref="Width"/>, <see cref="Height"/>) and maximizes per <see cref="Maximized"/>.
/// </summary>
public readonly record struct StartupPlacement(int X, int Y, int Width, int Height, bool Maximized, bool UseSaved);

/// <summary>
/// Shared window startup-placement policy, so every host restores saved geometry the same way: one
/// on-screen guard plus default fallback.
/// </summary>
public static class WindowPlacement {
	/// <summary>
	/// Use the saved bounds when present, validly sized, and still on-screen (intersecting at least one of
	/// <paramref name="screens"/>); otherwise fall back to a centered
	/// <paramref name="defaultWidth"/>×<paramref name="defaultHeight"/> window. The guard stops a window saved
	/// on a disconnected monitor from restoring off-screen. An empty <paramref name="screens"/> list skips the
	/// guard (the host couldn't enumerate monitors) and trusts the saved bounds.
	/// </summary>
	public static StartupPlacement Resolve(WindowState? saved, IReadOnlyList<PixelRect> screens, int defaultWidth, int defaultHeight) {
		ArgumentNullException.ThrowIfNull(screens);
		if (saved is { Width: > 0, Height: > 0 } s
			&& (screens.Count == 0
				|| screens.Any(screen => screen.IntersectsWith(new PixelRect(s.X, s.Y, s.Width, s.Height))))) {
			return new StartupPlacement(s.X, s.Y, s.Width, s.Height, s.Maximized, UseSaved: true);
		}

		return new StartupPlacement(0, 0, defaultWidth, defaultHeight, Maximized: false, UseSaved: false);
	}
}
