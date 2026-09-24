namespace RecipesManage.Domain.Recipes;

/// <summary>21 CFR 11 / GAMP 电子签名含义（签署时声明，不可由审核意见替代）。</summary>
public static class ElectronicSignature
{
    public const string ProcedureSave =
        "我作为工艺工程师确认本次 Procedure / Steps / Parameters 变更准确，并记录变更原因。";

    // 配方审核节点的签名含义不再由代码按枚举查表：那是审批链配置的一部分，
    // 提交时随整条链冻结进 approval_records（见 ApprovalChainStep）。

    public static string Batch(string action) => action switch
    {
        "batch.start.esign" =>
            "我作为操作员确认控制配方快照完整有效，启动本批四步握手，禁止盲写。",
        "batch.retry.esign" =>
            "我作为操作员确认故障已排除，从当前工步重新排队并恢复握手。",
        "batch.abort.esign" =>
            "我作为操作员确认中止本批，停止写参并释放设备占用。",
        "batch.hold.esign" =>
            "我作为操作员确认请求保持：写 Host_Hold，等待 PLC_Held，禁止盲写下一步。",
        "batch.resume.esign" =>
            "我作为操作员确认解除保持，从当前工步继续四步握手。",
        "batch.skip.esign" =>
            "我作为主管确认跳过当前工步：仅在未写参的就绪/等待/确认相位，禁止跨阶段盲写。",
        "batch.confirm.esign" =>
            "我作为操作员确认本工步人工确认点已核对，允许继续且本工步不写 PLC。",
        "batch.release.esign" =>
            "我作为质量审核人对照归档质检与四步握手，批准本批放行。",
        "batch.reject.esign" =>
            "我作为质量审核人对照归档质检与四步握手，拒收本批。",
        "lab.sample.dispose.esign" =>
            "我作为质量审核人对照规格判定本样品。",
        _ => "电子签名。"
    };

    public static string AuditDetail(string action, string? extra)
    {
        var meaning = Batch(action);
        return string.IsNullOrWhiteSpace(extra) ? meaning : $"{meaning} {extra.Trim()}";
    }
}
