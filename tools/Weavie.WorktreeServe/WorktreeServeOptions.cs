namespace Weavie.WorktreeServe;

internal sealed record WorktreeServeOptions(string? Workspace, int HttpsPort, string? StateRoot) {
	public const int DefaultHttpsPort = 10000;

	public const string Usage =
		"Usage: dotnet run --project tools/Weavie.WorktreeServe -- "
		+ "[--workspace <path>] [--https-port <port>] [--state-root <path>]";

	public static (WorktreeServeOptions? Options, string? Error) Resolve(string[] args) {
		ArgumentNullException.ThrowIfNull(args);
		string? workspace = null;
		string? stateRoot = null;
		int httpsPort = DefaultHttpsPort;

		for (int index = 0; index < args.Length; index += 2) {
			string name = args[index];
			if (index + 1 >= args.Length) {
				return (null, $"{name} requires a value.");
			}

			string value = args[index + 1];
			switch (name) {
				case "--workspace":
					workspace = value;
					break;
				case "--state-root":
					stateRoot = value;
					break;
				case "--https-port":
					if (!int.TryParse(value, out int parsed) || parsed is < 1 or > 65535) {
						return (null, $"invalid HTTPS port '{value}'.");
					}
					if (parsed is 443 or 8443) {
						return (null, $"HTTPS port {parsed} is reserved for Weavie Runner.");
					}
					httpsPort = parsed;
					break;
				default:
					return (null, $"unrecognized argument '{name}'.");
			}
		}

		return (new WorktreeServeOptions(workspace, httpsPort, stateRoot), null);
	}
}
