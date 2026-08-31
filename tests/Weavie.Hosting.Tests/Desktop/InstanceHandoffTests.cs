using Weavie.Hosting.Desktop;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// The handover a second launch performs. Both ends run in this process, so the pipe itself is covered without
/// a window: the failure it prevents is a double-click booting a whole second Weavie.
/// </summary>
public sealed class InstanceHandoffTests {
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

	[Fact]
	public void PipeNameIsPerRootAndShortEnoughForAUnixSocket() {
		string first = InstanceProtocol.PipeName("/home/a/.weavie");
		string second = InstanceProtocol.PipeName("/home/b/.weavie");

		Assert.NotEqual(first, second);
		Assert.Equal(first, InstanceProtocol.PipeName("/home/a/.weavie"));
		// macOS caps sun_path at 104 bytes and prefixes a long TMPDIR, so the name itself must stay small.
		Assert.True(first.Length <= 32, first);
	}

	[Fact]
	public async Task ARunningInstanceTakesThePathsAndTheCallerExits() {
		string root = NewRoot();
		var received = new TaskCompletionSource<IReadOnlyList<string>>(
			TaskCreationOptions.RunContinuationsAsynchronously);
		await using var server = new InstanceServer(
			root,
			paths => {
				received.TrySetResult(paths);
				return new HandoffReply(true, string.Empty);
			},
			_ => { });
		Assert.True(server.TryStart());

		var reply = await InstanceClient.OfferAsync(root, ["/tmp/a.ts"], CancellationToken.None);

		Assert.True(reply.Accepted);
		Assert.Equal(["/tmp/a.ts"], await received.Task.WaitAsync(Timeout));
	}

	[Fact]
	public async Task ADeclinedHandoverNamesTheWorkspaceTheCallerShouldBoot() {
		string root = NewRoot();
		await using var server = new InstanceServer(root, _ => new HandoffReply(false, "/repo"), _ => { });
		Assert.True(server.TryStart());

		var reply = await InstanceClient.OfferAsync(root, ["/repo/a.ts"], CancellationToken.None);

		Assert.False(reply.Accepted);
		Assert.Equal("/repo", reply.Root);
	}

	[Fact]
	public async Task ASecondHandoverReusesTheBoundInstance() {
		// HookBridgeServer's hard-won invariant: disconnect between connections, never dispose, or the second
		// connect races an unlinked socket file.
		string root = NewRoot();
		int handled = 0;
		await using var server = new InstanceServer(
			root,
			_ => {
				Interlocked.Increment(ref handled);
				return new HandoffReply(true, string.Empty);
			},
			_ => { });
		Assert.True(server.TryStart());

		Assert.True((await InstanceClient.OfferAsync(root, ["/tmp/a.ts"], CancellationToken.None)).Accepted);
		Assert.True((await InstanceClient.OfferAsync(root, ["/tmp/b.ts"], CancellationToken.None)).Accepted);
		Assert.Equal(2, handled);
	}

	[Fact]
	public async Task WithNoRunningInstanceTheCallerBootsItsOwn() {
		var reply = await InstanceClient.OfferAsync(NewRoot(), ["/tmp/a.ts"], CancellationToken.None);

		Assert.False(reply.Accepted);
		Assert.Equal(string.Empty, reply.Root);
	}

	[Fact]
	public async Task OnlyOneInstanceServesARoot() {
		// A second bind of the same name takes the endpoint over on Unix, so the first window would go deaf and
		// every later double-click would cold-boot another app.
		string root = NewRoot();
		await using var owner = new InstanceServer(root, _ => new HandoffReply(true, "owner"), _ => { });
		await using var second = new InstanceServer(root, _ => new HandoffReply(true, "second"), _ => { });

		Assert.True(owner.TryStart());
		Assert.False(second.TryStart());
		Assert.Equal("owner", (await InstanceClient.OfferAsync(root, ["/tmp/a.ts"], CancellationToken.None)).Root);
	}

	[Fact]
	public async Task AThrowingHandlerStillAnswers() {
		// An unanswered caller silently boots a second app.
		string root = NewRoot();
		await using var server = new InstanceServer(root, _ => throw new InvalidOperationException("boom"), _ => { });
		Assert.True(server.TryStart());

		Assert.False((await InstanceClient.OfferAsync(root, ["/tmp/a.ts"], CancellationToken.None)).Accepted);
	}

	private static string NewRoot() {
		string root = Path.Combine(Path.GetTempPath(), $"weavie-instance-{Guid.NewGuid():N}");
		Directory.CreateDirectory(root);
		return root;
	}
}
