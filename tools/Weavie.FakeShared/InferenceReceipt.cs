namespace Weavie.Fakes;

/// <summary>Records the last inference query a fake answered, for tests to read back.</summary>
internal static class InferenceReceipt {
	/// <summary>
	/// Replaces <c>inference-receipt.txt</c> in <paramref name="directory"/> whole. Weavie runs inferences in parallel
	/// (one model suggestion per workflow role), and two fakes writing the file in place collide on Windows.
	/// </summary>
	public static async Task WriteAsync(string directory, string query) {
		string staged = Path.Combine(directory, $"inference-receipt.{Environment.ProcessId}.tmp");
		await File.WriteAllTextAsync(staged, query).ConfigureAwait(false);
		File.Move(staged, Path.Combine(directory, "inference-receipt.txt"), true);
	}
}
