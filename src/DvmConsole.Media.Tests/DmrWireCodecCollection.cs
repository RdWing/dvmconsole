using Xunit;

namespace DvmConsole.Media.Tests;

// The legacy fnecore DMR codec uses shared scratch state during encode/decode.
// Keep the tests that exercise that wire codec together while leaving unrelated
// media tests eligible for normal xUnit parallel execution.
[CollectionDefinition("DMR wire codec", DisableParallelization = true)]
public sealed class DmrWireCodecCollection
{
}
