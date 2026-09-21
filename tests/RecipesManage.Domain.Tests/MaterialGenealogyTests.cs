using RecipesManage.Domain.Batches;
using RecipesManage.Domain.Materials;
using Xunit;

namespace RecipesManage.Domain.Tests;

public sealed class MaterialGenealogyTests
{
    [Fact]
    public void Split_CreatesChildAndReducesParentQuantity()
    {
        var parent = MaterialLot.Receive("INGOT-01", "AL6061", "铝锭", 100, "kg");
        var child = parent.Split("INGOT-01-S1", 40);
        Assert.Equal(60, parent.Quantity);
        Assert.Equal(MaterialLotSource.Split, child.Source);
        Assert.Equal(parent.Id, child.ParentLotId);
        Assert.Equal(40, child.Quantity);
        Assert.Equal("AL6061", child.MaterialCode);
    }

    [Fact]
    public void Split_RejectsMoreThanRemaining()
    {
        var parent = MaterialLot.Receive("INGOT-02", "AL6061", "铝锭", 10, "kg");
        Assert.Throws<RecipesManage.Domain.Common.DomainException>(() => parent.Split("INGOT-02-S1", 11));
    }

    [Fact]
    public void Genealogy_WalksAncestorsAndDescendants()
    {
        var root = MaterialLot.Receive("ROOT", "AL6061", "铝锭", 100, "kg");
        var mid = root.Split("MID", 50);
        var leaf = mid.Split("LEAF", 10);
        var extra = root.Split("SIDE", 5);
        var byId = new[] { root, mid, leaf, extra }.ToDictionary(l => l.Id);
        var ancestors = MaterialGenealogy.Ancestors(leaf, byId);
        Assert.Equal(["ROOT", "MID"], ancestors.Select(a => a.LotNumber).ToArray());
        var down = MaterialGenealogy.Descendants(root.Id, byId.Values.ToList());
        Assert.Contains(down, d => d.LotNumber == "MID");
        Assert.Contains(down, d => d.LotNumber == "LEAF");
        Assert.Contains(down, d => d.LotNumber == "SIDE");
    }

    [Fact]
    public void LabSample_FailRequiresComment_PendingFinalBlocksReleaseGate()
    {
        var sample = new LabSample("QC-1", Guid.NewGuid(), LabSampleType.Final, "qa", DateTimeOffset.UtcNow);
        Assert.True(QualityDisposition.HasPendingFinalSample([sample]));
        Assert.Throws<RecipesManage.Domain.Common.DomainException>(() =>
            sample.RecordDisposition(LabSampleDisposition.Fail, "qa", "", DateTimeOffset.UtcNow));
        sample.RecordDisposition(LabSampleDisposition.Fail, "qa", "硬度低于规格", DateTimeOffset.UtcNow);
        Assert.True(QualityDisposition.HasFailedLabSample([sample]));
        Assert.False(QualityDisposition.HasPendingFinalSample([sample]));
    }
}
