using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Weavie.Runner.Tests;

public sealed class HeadlessLauncherTests {
	private const string Loopback = "127.0.0.1";

	private static WorkspaceBackend Backend(bool pinned, int port) =>
		new() {
			WorkspaceRoot = "/tmp/workspace",
			Port = port,
			PortIsPinned = pinned,
			Token = "token",
		};

	// The failure this decides (kapps/weavie#461, and again in run 32798395828/job 97654725603): the worker was
	// handed a port another listener already held, so it never came up, and every relaunch retried that same
	// doomed port until the supervisor gave up.
	[Fact]
	public void ShouldRepickPort_WhenAnotherListenerHoldsIt() {
		int port = BackendManager.AllocatePort();
		var squatter = new TcpListener(IPAddress.Parse(Loopback), port);
		squatter.Start();
		try {
			Assert.True(HeadlessLauncher.ShouldRepickPort(Backend(pinned: false, port), Loopback));
		} finally {
			squatter.Stop();
		}
	}

	// A worker that died for its own reasons finds its port free, and keeps it so a crash-restart lands where
	// connected browsers are already looking.
	[Fact]
	public void ShouldRepickPort_KeepsAPortNothingIsListeningOn() =>
		Assert.False(
			HeadlessLauncher.ShouldRepickPort(Backend(pinned: false, BackendManager.AllocatePort()), Loopback));

	// --worker-port is the TLS front's mapping; repicking it would strand the front on a dead port, so a pinned
	// port stays put and the launch fails loudly on the conflict instead.
	[Fact]
	public void ShouldRepickPort_NeverRepicksAPinnedPort() {
		int port = BackendManager.AllocatePort();
		var squatter = new TcpListener(IPAddress.Parse(Loopback), port);
		squatter.Start();
		try {
			Assert.False(HeadlessLauncher.ShouldRepickPort(Backend(pinned: true, port), Loopback));
		} finally {
			squatter.Stop();
		}
	}

	[Fact]
	public void PortIsFree_TracksWhetherAListenerHoldsThePort() {
		int port = BackendManager.AllocatePort();
		Assert.True(HeadlessLauncher.PortIsFree(Loopback, port));

		var listener = new TcpListener(IPAddress.Parse(Loopback), port);
		listener.Start();
		try {
			Assert.False(HeadlessLauncher.PortIsFree(Loopback, port));
		} finally {
			listener.Stop();
		}

		// Released again: a repick must not fire on a port whose listener has gone away.
		Assert.True(HeadlessLauncher.PortIsFree(Loopback, port));
	}

	// --worker-bind allows "localhost", which is not a literal address the socket layer can take as one.
	[Fact]
	public void PortIsFree_AcceptsLocalhostAsABindAddress() {
		int port = BackendManager.AllocatePort();
		var listener = new TcpListener(IPAddress.Loopback, port);
		listener.Start();
		try {
			Assert.False(HeadlessLauncher.PortIsFree("localhost", port));
		} finally {
			listener.Stop();
		}
	}
}
