namespace Weavie.Core.Git;

/// <summary>The decoded content, existence, and text classification of a file at a git ref.</summary>
/// <param name="Content">The file text, empty for a binary, empty, or absent file.</param>
/// <param name="Exists">Whether the path exists at the ref.</param>
/// <param name="IsText">Whether an existing path is valid text according to the workspace text reader.</param>
public sealed record GitFileContent(string Content, bool Exists, bool IsText);
