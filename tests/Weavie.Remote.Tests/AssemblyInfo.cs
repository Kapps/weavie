using Xunit;

// Each test launches a real host process; run them serially so port allocation and process startup don't
// contend, and the machine isn't asked to spin up many .NET hosts at once.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
