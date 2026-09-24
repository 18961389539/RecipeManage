using RecipesManage.Domain.Common;
using RecipesManage.Domain.Recipes;
using Xunit;

namespace RecipesManage.Domain.Tests;

public sealed class PlcProgramTests
{
    [Fact]
    public void Resolve_FallsBackToEnumInteger()
    {
        Assert.Equal(1, PlcProgram.Resolve(StepType.Heat, null));
        Assert.Equal(21, PlcProgram.Resolve(StepType.Transfer, 21));
        Assert.Equal(0, PlcProgram.Resolve(StepType.Wait, null));
    }

    [Fact]
    public void WritesToPlc_HostKindsDoNotWrite()
    {
        Assert.False(PlcProgram.WritesToPlc(StepType.Wait));
        Assert.False(PlcProgram.WritesToPlc(StepType.QualityCheck));
        Assert.False(PlcProgram.WritesToPlc(StepType.ManualConfirm));
        Assert.True(PlcProgram.WritesToPlc(StepType.Heat));
        Assert.True(PlcProgram.WritesToPlc(StepType.Transfer));
        Assert.Equal(ExecutionKind.WritePlc, PlcProgram.Kind(StepType.Heat));
        Assert.Equal(ExecutionKind.WritePlc, PlcProgram.Kind(StepType.Transfer));
        Assert.Equal(ExecutionKind.Wait, PlcProgram.Kind(StepType.Wait));
        Assert.Equal(ExecutionKind.QualityCheck, PlcProgram.Kind(StepType.QualityCheck));
        Assert.Equal(ExecutionKind.ManualConfirm, PlcProgram.Kind(StepType.ManualConfirm));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(8)]
    public void ProcessStep_RejectsHostReservedProgramIds(int id)
    {
        var ex = Assert.Throws<DomainException>(() => PlcProgram.EnsureValid(StepType.Pressure, id));
        Assert.Equal("PLC_PROGRAM", ex.Code);
    }

    [Fact]
    public void HostStep_RejectsForeignProgramId()
    {
        var ex = Assert.Throws<DomainException>(() => PlcProgram.EnsureValid(StepType.Wait, 21));
        Assert.Equal("PLC_PROGRAM", ex.Code);
    }

    [Fact]
    public void CustomProgram_NineToNinetyNine_IsAllowed()
    {
        PlcProgram.EnsureValid(StepType.Transfer, 21);
        PlcProgram.EnsureValid(StepType.Pressure, 22);
        PlcProgram.EnsureValid(StepType.Heat, 1);
    }
}
