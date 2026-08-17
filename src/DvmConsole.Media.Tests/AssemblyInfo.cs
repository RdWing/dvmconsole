using Xunit;

// fnecore's legacy DMR codecs use process-wide mutable state and are not safe
// to exercise concurrently from separate xUnit test classes.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
