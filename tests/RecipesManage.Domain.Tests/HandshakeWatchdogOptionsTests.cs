using RecipesManage.Domain.Handshake;
using Xunit;

namespace RecipesManage.Domain.Tests;

public sealed class HandshakeWatchdogOptionsTests
{
    [Fact]
    public void FromJson_ReadsCamelCaseSeconds()
    {
        var options = HandshakeWatchdogOptions.FromJson("""{"readyWaitSeconds":21,"ackTimeoutSeconds":9}""");
        Assert.Equal(TimeSpan.FromSeconds(21), options.ReadyWaitTimeout);
        Assert.Equal(TimeSpan.FromSeconds(9), options.AckTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), options.WriteTimeout);
    }

    [Fact]
    public void FromJson_EmptyUsesDefaults()
    {
        var options = HandshakeWatchdogOptions.FromJson(null);
        Assert.Equal(TimeSpan.FromSeconds(15), options.ReadyWaitTimeout);
        Assert.Equal(TimeSpan.FromSeconds(8), options.HoldAckTimeout);
    }

    [Fact]
    public void FromJson_ReadsHoldAckSeconds()
    {
        var options = HandshakeWatchdogOptions.FromJson("""{"holdAckSeconds":6}""");
        Assert.Equal(TimeSpan.FromSeconds(6), options.HoldAckTimeout);
    }
}
