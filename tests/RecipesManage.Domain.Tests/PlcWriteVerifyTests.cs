using RecipesManage.Domain.Handshake;
using Xunit;

namespace RecipesManage.Domain.Tests;

public sealed class PlcWriteVerifyTests
{
    [Fact]
    public void Matches_WhenEchoEqualsWrite()
    {
        float[] written = [530f, 8f, 0f];
        var echo = new PlcStepPayload(10, 1, written);
        Assert.True(PlcWriteVerify.Matches(10, 1, written, echo, out var mismatch));
        Assert.Equal("", mismatch);
    }

    [Fact]
    public void Rejects_StepIdMismatch()
    {
        float[] written = [530f];
        var echo = new PlcStepPayload(11, 1, written);
        Assert.False(PlcWriteVerify.Matches(10, 1, written, echo, out var mismatch));
        Assert.Contains("Step_ID", mismatch);
    }

    [Fact]
    public void Rejects_ParamDriftBeyondEpsilon()
    {
        float[] written = [530f, 8f];
        var echo = new PlcStepPayload(10, 1, [530.2f, 8f]);
        Assert.False(PlcWriteVerify.Matches(10, 1, written, echo, out var mismatch));
        Assert.Contains("Param[0]", mismatch);
    }
}
