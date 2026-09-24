using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Recipes;
using Xunit;

namespace RecipesManage.Domain.Tests;

public sealed class QualityDispositionTests
{
    [Fact]
    public void HasOutOfSpec_WhenArchivedValueExceedsMax()
    {
        var stepId = Guid.NewGuid();
        var snapshot = new ControlRecipeSnapshot
        {
            Steps =
            [
                new SnapshotStep
                {
                    StepId = stepId,
                    Code = "S20",
                    Name = "qc",
                    Type = StepType.QualityCheck,
                    Parameters =
                    [
                        new SnapshotParameter { Name = "硬度", EngineeringUnit = "HB", Setpoint = 90, Min = 80, Max = 100, ArchiveAsQuality = true }
                    ]
                }
            ]
        };
        var exec = new BatchStepExecution(Guid.NewGuid(), stepId, "S20", "qc", StepType.QualityCheck, 1);
        exec.MarkCompleted(DateTimeOffset.UtcNow, """{"硬度":120}""");
        Assert.True(QualityDisposition.HasOutOfSpec(snapshot, [exec]));
    }

    [Fact]
    public void HasOutOfSpec_IgnoresPlcTaggedChannels()
    {
        var stepId = Guid.NewGuid();
        var snapshot = new ControlRecipeSnapshot
        {
            Steps =
            [
                new SnapshotStep
                {
                    StepId = stepId,
                    Code = "S10",
                    Name = "heat",
                    Type = StepType.Heat,
                    Parameters =
                    [
                        new SnapshotParameter { Name = "目标温度", EngineeringUnit = "℃", Setpoint = 120, Min = 100, Max = 200, ArchiveAsQuality = true }
                    ]
                }
            ]
        };
        var exec = new BatchStepExecution(Guid.NewGuid(), stepId, "S10", "heat", StepType.Heat, 0);
        exec.MarkCompleted(DateTimeOffset.UtcNow, """{"目标温度":150,"PLC:Temperature":999}""");
        Assert.False(QualityDisposition.HasOutOfSpec(snapshot, [exec]));
    }

    [Fact]
    public void HasOutOfSpec_WhenQualityParamNeverMeasured()
    {
        var stepId = Guid.NewGuid();
        var snapshot = new ControlRecipeSnapshot
        {
            Steps =
            [
                new SnapshotStep
                {
                    StepId = stepId,
                    Code = "S50",
                    Name = "qc",
                    Type = StepType.QualityCheck,
                    Parameters =
                    [
                        new SnapshotParameter { Name = "硬度", EngineeringUnit = "HB", Setpoint = 95, Min = 90, Max = 110, ArchiveAsQuality = true }
                    ]
                }
            ]
        };
        var exec = new BatchStepExecution(Guid.NewGuid(), stepId, "S50", "qc", StepType.QualityCheck, 0);
        exec.MarkCompleted(DateTimeOffset.UtcNow, """{"PLC:Temperature":530}""");
        Assert.True(QualityDisposition.HasOutOfSpec(snapshot, [exec]));
    }
}
