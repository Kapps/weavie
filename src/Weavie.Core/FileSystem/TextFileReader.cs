using System.Text;

namespace Weavie.Core.FileSystem;

internal static class TextFileReader {
	private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

	public static bool TryRead(Stream stream, out string contents) {
		ArgumentNullException.ThrowIfNull(stream);
		using StreamReader reader = new(stream, StrictUtf8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
		StringBuilder result = new();
		char[] buffer = new char[4096];
		try {
			int read;
			while ((read = reader.Read(buffer, 0, buffer.Length)) > 0) {
				if (Array.IndexOf(buffer, '\0', 0, read) >= 0) {
					contents = string.Empty;
					return false;
				}

				result.Append(buffer, 0, read);
			}
		} catch (DecoderFallbackException) {
			contents = string.Empty;
			return false;
		}

		contents = result.ToString();
		return true;
	}
}
