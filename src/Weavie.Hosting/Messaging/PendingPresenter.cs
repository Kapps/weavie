namespace Weavie.Hosting.Messaging;

/// <summary>Who shows the user that an inbound operation is still running.</summary>
internal enum PendingPresenter {
	/// <summary>The bus raises a busy notification once the operation passes the slow threshold.</summary>
	Bus,

	/// <summary>The requesting surface shows its own in-place progress; only the deadline is reported.</summary>
	Caller,
}
