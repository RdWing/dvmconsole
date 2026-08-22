using DvmConsole.FneClient;
using fnecore;
using Xunit;

namespace DvmConsole.FneClient.Tests;

public sealed class FnePeerSessionFactoryTests
{
    [Theory]
    [InlineData(FneTransportEncryptionPreference.Auto, FneTransportEncryptionMode.Auto)]
    [InlineData(FneTransportEncryptionPreference.Ecb, FneTransportEncryptionMode.Ecb)]
    [InlineData(FneTransportEncryptionPreference.Cbc, FneTransportEncryptionMode.Cbc)]
    public void MapsConfiguredTransportPreference(
        FneTransportEncryptionPreference preference,
        FneTransportEncryptionMode expected)
        => Assert.Equal(expected, FnePeerSessionFactory.ToTransportMode(preference));
}
