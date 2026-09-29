using System.Text.Json.Serialization;

namespace Weavie.Hosting;

/// <summary>Authoritative file-reference resolution without editor or focus side effects.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(FileReferenceResolution.File), "file")]
[JsonDerivedType(typeof(FileReferenceResolution.Ambiguous), "ambiguous")]
[JsonDerivedType(typeof(FileReferenceResolution.Missing), "missing")]
public abstract record FileReferenceResolution {
	/// <summary>A readable absolute file and its optional, clamped reveal line.</summary>
	public sealed record File(string Path, int? Line) : FileReferenceResolution;

	/// <summary>A relative reference requiring an explicit choice among indexed files.</summary>
	public sealed record Ambiguous(string Query, int? Line) : FileReferenceResolution;

	/// <summary>A resolution failure to display to the requester.</summary>
	public sealed record Missing(string Message) : FileReferenceResolution;
}
