using System.Text;

namespace Weavie.Core.Processes;

/// <summary>Decodes captured process output the way a UTF-8 <see cref="StreamReader"/> over the pipe would.</summary>
public static class ProcessOutput {
	private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

	/// <summary>Decodes <paramref name="bytes"/> as UTF-8, letting a leading byte-order mark select its encoding.</summary>
	public static string Decode(byte[] bytes) => Decode(bytes, 0, bytes.Length);

	/// <summary>Decodes <paramref name="count"/> bytes of <paramref name="bytes"/> from <paramref name="offset"/>.</summary>
	public static string Decode(byte[] bytes, int offset, int count) {
		if (count == 0) return string.Empty;
		using var reader = new StreamReader(new MemoryStream(bytes, offset, count, writable: false), Utf8, detectEncodingFromByteOrderMarks: true);
		return reader.ReadToEnd();
	}
}
