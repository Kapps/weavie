namespace Weavie.Hosting;

/// <summary>
/// A raster image read from the OS clipboard: the encoded <see cref="Bytes"/> and their <see cref="Mime"/> type.
/// <see cref="Mime"/> is empty (see <see cref="None"/>) when the clipboard holds no image.
/// </summary>
public readonly record struct ClipboardImage(string Mime, byte[] Bytes) {
	/// <summary>The absence of a clipboard image — an empty MIME and no bytes.</summary>
	public static ClipboardImage None => new(string.Empty, []);
}
