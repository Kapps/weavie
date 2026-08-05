namespace Weavie.Core.Configuration;

/// <summary>Settings that gate stateless ad-hoc model queries.</summary>
public static class InferenceSettings {
	/// <summary>Whether any ad-hoc inference operation may call its provider.</summary>
	public const string Enabled = "inference.enabled";

	/// <summary>Whether application-initiated inference may spend tokens without a direct user action.</summary>
	public const string AllowAutomatic = "inference.allowAutomatic";
}
