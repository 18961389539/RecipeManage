namespace RecipesManage.Domain.Recipes;

/// <summary>21 CFR 11 / GAMP 电子签名含义（签署时声明，不可由审核意见替代）。</summary>
public static class ElectronicSignature
{
    public const string ProcedureSave =
        "我作为工艺工程师确认本次 Procedure / Steps / Parameters 变更准确，并记录变更原因。";

    public static string Meaning(ApprovalLevel level, ApprovalDecision decision) =>
        (level, decision) switch
        {
            (ApprovalLevel.Author, ApprovalDecision.Approved) =>
                "我作为工艺工程师确认本版本 Procedure / Steps 与 Parameters / Setpoints 准确，提交多级审核。",
            (ApprovalLevel.Author, _) =>
                "工艺工程师提交审核。",
            (ApprovalLevel.Supervisor, ApprovalDecision.Approved) =>
                "我作为工艺主管确认工艺路径可执行，批准进入质量审核。",
            (ApprovalLevel.Supervisor, ApprovalDecision.Rejected) =>
                "我作为工艺主管驳回：工艺路径不可执行或需要返工。",
            (ApprovalLevel.Supervisor, _) =>
                "待工艺主管签署：确认工艺路径可执行。",
            (ApprovalLevel.Quality, ApprovalDecision.Approved) =>
                "我作为质量审核人确认参数窗口可接受，批准本版本作为生效主配方。",
            (ApprovalLevel.Quality, ApprovalDecision.Rejected) =>
                "我作为质量审核人驳回：参数窗口不可接受或需要返工。",
            (ApprovalLevel.Quality, _) =>
                "待质量签署：确认参数窗口可接受。",
            _ => "电子签名。"
        };
}
