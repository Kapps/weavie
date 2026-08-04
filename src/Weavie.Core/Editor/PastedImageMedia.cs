namespace Weavie.Core.Editor;

/// <summary>
/// The single source of truth for images pasted into an agent: the allowed MIME types (with the extension each is
/// saved under) and the per-image byte cap. The paste handler validates against this; the web mirrors the same
/// values for its pre-check.
/// </summary>
public static class PastedImageMedia {
	/// <summary>Maximum bytes for one pasted agent image; a larger paste is rejected with a surfaced message.</summary>
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

	/// <summary>Validates and decodes one supported pasted image, returning its file extension and bytes.</summary>
	public static (string Extension, byte[] Bytes) Decode(string mime, string dataB64) {
		ArgumentNullException.ThrowIfNull(mime);
		ArgumentNullException.ThrowIfNull(dataB64);
		if (!TryExtension(mime, out string extension)) {
			throw new InvalidOperationException(
				$"Can't paste that image type ({(mime.Length == 0 ? "unknown" : mime)}) — use PNG, JPEG, GIF, or WebP.");
		}

		long approximateBytes = (long)dataB64.Length / 4 * 3;
		if (approximateBytes > MaxBytes) {
			throw new InvalidOperationException(
				$"That image is {approximateBytes / (1024.0 * 1024.0):0.0} MB — Weavie accepts agent images up to {MaxBytes / (1024 * 1024)} MB.");
		}

		byte[] bytes = Convert.FromBase64String(dataB64);
		if (bytes.Length == 0) {
			throw new InvalidOperationException("The pasted image was empty.");
		}

		return (extension, bytes);
	}
}
