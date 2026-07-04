namespace Weavie.Core.Editor;

/// <summary>
/// The single source of truth for images pasted into Claude: the allowed MIME types (with the extension each is
/// saved under) and the per-image byte cap. The paste handler validates against this; the web mirrors the same
/// values for its pre-check.
/// </summary>
public static class PastedImageMedia {
	/// <summary>Maximum bytes for one pasted image (Claude's per-image limit); a larger paste is rejected with a surfaced message.</summary>
	public const long MaxBytes = 5 * 1024 * 1024;

	/// <summary>
	/// Maps a paste's MIME type to the file extension to save it under, returning whether it is an allowed image
	/// type (png/jpeg/gif/webp). A disallowed type yields <see langword="false"/> and an empty extension.
	/// </summary>
	public static bool TryExtension(string mime, out string extension) {
		extension = mime switch {
			"image/png" => ".png",
			"image/jpeg" => ".jpg",
			"image/gif" => ".gif",
			"image/webp" => ".webp",
			_ => string.Empty,
		};
		return extension.Length > 0;
	}
}
