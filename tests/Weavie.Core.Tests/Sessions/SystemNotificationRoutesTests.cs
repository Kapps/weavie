using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Core.Tests.Sessions;

public sealed class SystemNotificationRoutesTests {
	[Fact]
	public void Replace_RejectsStaleActivation_AndRoutesCurrentActivation() {
		var routes = new SystemNotificationRoutes<object>();
		object owner = new();
		routes.Replace(owner, Notification("replacement", "old")).Commit();
		routes.Replace(owner, Notification("replacement", "current")).Commit();

		Assert.False(routes.TryTake("old", out _));
		Assert.True(routes.TryTake("current", out object? activated));
		Assert.Same(owner, activated);
	}

	[Fact]
	public void Rollback_RestoresPreviousRoute() {
		var routes = new SystemNotificationRoutes<object>();
		object owner = new();
		routes.Replace(owner, Notification("replacement", "previous")).Commit();
		var failed = routes.Replace(owner, Notification("replacement", "failed"));

		failed.Rollback();

		Assert.False(routes.TryTake("failed", out _));
		Assert.True(routes.TryTake("previous", out object? activated));
		Assert.Same(owner, activated);
	}

	[Fact]
	public void Replace_KeepsPreviousRouteUntilCommit() {
		var routes = new SystemNotificationRoutes<object>();
		object owner = new();
		routes.Replace(owner, Notification("before-commit", "old-before")).Commit();
		_ = routes.Replace(owner, Notification("before-commit", "new-before"));
		routes.Replace(owner, Notification("after-commit", "old-after")).Commit();
		var committed = routes.Replace(owner, Notification("after-commit", "new-after"));

		Assert.True(routes.TryTake("old-before", out object? activated));
		Assert.Same(owner, activated);
		committed.Commit();

		Assert.False(routes.TryTake("old-after", out _));
		Assert.True(routes.TryTake("new-after", out activated));
		Assert.Same(owner, activated);
	}

	[Fact]
	public void Rollback_DoesNotResurrectAnOwnerThatWasForgotten() {
		var routes = new SystemNotificationRoutes<object>();
		object owner = new();
		routes.Replace(owner, Notification("replacement", "previous")).Commit();
		var failed = routes.Replace(owner, Notification("replacement", "failed"));

		Assert.Equal(["replacement"], routes.ForgetOwner(owner));
		failed.Rollback();

		Assert.False(routes.TryTake("previous", out _));
		Assert.False(routes.TryTake("failed", out _));
	}

	[Fact]
	public void ForgetOwner_UsesOwnerIdentity_AndReturnsEveryReplacement() {
		var routes = new SystemNotificationRoutes<EqualOwner>();
		EqualOwner owner = new();
		EqualOwner equalButDistinctOwner = new();
		routes.Replace(owner, Notification("one", "activation-one")).Commit();
		routes.Replace(owner, Notification("two", "activation-two")).Commit();
		routes.Replace(equalButDistinctOwner, Notification("three", "activation-three")).Commit();

		Assert.Equal(["one", "two"], routes.ForgetOwner(owner).Order());
		Assert.False(routes.TryTake("activation-one", out _));
		Assert.False(routes.TryTake("activation-two", out _));
		Assert.True(routes.TryTake("activation-three", out var activated));
		Assert.Same(equalButDistinctOwner, activated);
	}

	private static SystemNotification Notification(string replacementId, string activationId) =>
		new(replacementId, activationId, "Title", "Body");

	private sealed class EqualOwner {
		public override bool Equals(object? obj) => obj is EqualOwner;

		public override int GetHashCode() => 0;
	}
}
