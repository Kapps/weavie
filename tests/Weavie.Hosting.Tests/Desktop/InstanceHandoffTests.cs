using System.Net.Sockets;
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
			request => {
				received.TrySetResult(request.Paths);
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
	public async Task TryStartDoesNotReturnUntilTheListenerPoolIsBound() {
		string root = NewRoot();
		using var entered = new ManualResetEventSlim();
		using var release = new ManualResetEventSlim();
		int opened = 0;
		await using var server = new InstanceServer(
			root,
			_ => new HandoffReply(true, "owner"),
			_ => { },
			pipeName => {
				if (Interlocked.Increment(ref opened) == 1) {
					entered.Set();
					release.Wait();
				}
				return InstanceServer.OpenListener(pipeName);
			});

		var starting = Task.Run(server.TryStart);
		try {
			Assert.True(entered.Wait(Timeout));
			Assert.False(starting.IsCompleted);
		} finally {
			release.Set();
		}

		Assert.True(await starting.WaitAsync(Timeout));
		Assert.Equal(4, opened);
		Assert.Equal(
			"owner",
			(await InstanceClient.OfferAsync(root, ["/tmp/a.ts"], CancellationToken.None)).Root);
	}

	[Fact]
	public async Task APartialListenerBindFailureAlwaysReleasesTheRoot() {
		await AssertFailedStartReleasesRoot(new IOException("bind failed"), false);
		await AssertFailedStartReleasesRoot(
			new SocketException((int)SocketError.AddressAlreadyInUse),
			false);
		await AssertFailedStartReleasesRoot(new InvalidOperationException("unexpected"), true);
	}

	[Fact]
	public async Task TheActivationTokenTravelsWithTheHandover() {
		// The launch that received the click owns the compositor's token; the running window needs it to raise.
		string root = NewRoot();
		Environment.SetEnvironmentVariable("XDG_ACTIVATION_TOKEN", "token-123");
		try {
			var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
			await using var server = new InstanceServer(
				root,
				request => {
					seen.TrySetResult(request.ActivationToken);
					return new HandoffReply(true, string.Empty);
				},
				_ => { });
			Assert.True(server.TryStart());

			await InstanceClient.OfferAsync(root, ["/tmp/a.ts"], CancellationToken.None);

			Assert.Equal("token-123", await seen.Task.WaitAsync(Timeout));
		} finally {
			Environment.SetEnvironmentVariable("XDG_ACTIVATION_TOKEN", null);
		}
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

	private static async Task AssertFailedStartReleasesRoot(Exception failure, bool throws) {
		string root = NewRoot();
		int opened = 0;
		await using var failed = new InstanceServer(
			root,
			_ => new HandoffReply(true, "failed"),
			_ => { },
			pipeName => Interlocked.Increment(ref opened) == 2
				? throw failure
				: InstanceServer.OpenListener(pipeName));
		if (throws) {
			Assert.Same(
				failure,
				Assert.Throws(failure.GetType(), () => {
					failed.TryStart();
				}));
		} else {
			Assert.False(failed.TryStart());
		}

		await using var replacement = new InstanceServer(
			root,
			_ => new HandoffReply(true, "replacement"),
			_ => { });
		Assert.True(replacement.TryStart());
		Assert.Equal(
			"replacement",
			(await InstanceClient.OfferAsync(root, ["/tmp/a.ts"], CancellationToken.None)).Root);
	}
}
