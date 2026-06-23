namespace Weavie.Hosting;

/// <summary>
/// The unattended screenshot request from the <c>WEAVIE_SHOT_*</c> env vars, off unless <c>WEAVIE_SHOT_DIR</c> is
/// set (so the shipped app never writes screenshots). Shared so every host gates + defaults identically.
/// </summary>
public sealed record ScreenshotRequest(string DirectoryPath, double DelaySeconds, string TargetPath) {
	private const string DefaultName = "step1-latency.png";
	private const double DefaultDelaySeconds = 4.0;

	/// <summary>The pending request read from the environment, or <c>null</c> when <c>WEAVIE_SHOT_DIR</c> is unset (the normal case).</summary>
	public static ScreenshotRequest? FromEnvironment() {
		string? dir = Environment.GetEnvironmentVariable("WEAVIE_SHOT_DIR");
		if (string.IsNullOrEmpty(dir)) {
			return null;
		}

		double delay = double.TryParse(Environment.GetEnvironmentVariable("WEAVIE_SHOT_DELAY"), out double d) ? d : DefaultDelaySeconds;
		string name = Environment.GetEnvironmentVariable("WEAVIE_SHOT_NAME") is { Length: > 0 } n ? n : DefaultName;
		return new ScreenshotRequest(dir, delay, Path.Combine(dir, name));
	}
}
