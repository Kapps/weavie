using Xunit;

// Each test launches a real host process; run serially so port allocation and startup don't contend.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
