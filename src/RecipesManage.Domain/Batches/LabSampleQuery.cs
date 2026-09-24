namespace RecipesManage.Domain.Batches;

/// <summary>
/// 「待判终样」的唯一口径。
///
/// 为什么要有这个文件：总览磁贴（EquipmentService.DashboardAsync）与批次列表的
/// <c>onlyLabPending</c> 筛选（BatchService.ListAsync）必须数同一批东西，否则磁贴显示 3、
/// 点进去列表只有 2 条——一个批次可能挂着多个待判终样，两边一个数样品、一个数批次就必然对不上。
/// 这种"界面自己跟自己对账"的错，靠 review 很难看出来，只能把谓词收成一处再拿测试钉住。
///
/// 写成 IQueryable 扩展而不是 Expression 字段：它在组装期就被内联成 Where，
/// EF 仍然能把整条查询翻成 SQL（换成自定义方法包一层就会翻译失败）。
/// </summary>
public static class LabSampleQuery
{
    public static IQueryable<LabSample> PendingFinal(this IQueryable<LabSample> samples) =>
        samples.Where(s => s.SampleType == LabSampleType.Final
                           && s.Disposition == LabSampleDisposition.Pending);

    /// <summary>这一批里有哪些批次还欠着终样判定。</summary>
    public static IQueryable<Guid> BatchIdsPendingFinal(this IQueryable<LabSample> samples, IEnumerable<Guid> batchIds)
    {
        var ids = batchIds.Distinct().ToList();
        return samples.PendingFinal()
            .Where(s => ids.Contains(s.BatchId))
            .Select(s => s.BatchId)
            .Distinct();
    }
}
