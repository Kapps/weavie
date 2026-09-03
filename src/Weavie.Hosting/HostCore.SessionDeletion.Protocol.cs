using System.Text.Json;
using Weavie.Core.Commands;
using Weavie.Core.Sessions;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private Task<CommandResult> InvokeDeleteSessionAsync(
		HostSession? source,
		JsonElement? args,
		CommandInvocationContext context,
		CancellationToken ct) {
		try {
			var invocation = DeleteSessionProtocol.Parse(args ?? default);
			return invocation.Operation switch {
				DeleteSessionOperation.Preview => PreviewDeleteSessionAsync(invocation.Id, ct),
				DeleteSessionOperation.Confirm => ConfirmDeleteSessionAsync(
					source,
					invocation.Id,
					invocation.Confirmation!,
					context,
					ct),
				_ => throw new InvalidOperationException("Unknown Delete Session operation."),
			};
		} catch (JsonException ex) {
			return Task.FromResult(CommandResult.Failure(
				$"Invalid Delete Session arguments: {ex.Message}"));
		}
	}
}
