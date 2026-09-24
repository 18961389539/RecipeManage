namespace RecipesManage.Domain.Common;

/// <summary>
/// 文本完整性检查：拒绝"编码转换丢字"特征的输入。
///
/// 背景：开发库里有一条主配方，名称是 <c>???????</c>、工步名是 <c>??</c>。
/// 全仓没有任何非 UTF-8 的编码路径（无 Encoding.ASCII / Default），所以不是应用写坏的，
/// 而是某个客户端把 UTF-8 串编进了非 Unicode 代码页再 POST —— 每个汉字变成一个 <c>?</c>。
/// 这种丢失在数据库里**不可逆**，问号就是问号；而且它会随控制配方快照冻结进批次，
/// 电子批记录打印出来产品名就是五个问号。
///
/// 所以只能在入口拒绝。判定保守：只认"整串都是问号"和"连续两个以上问号"，
/// 单个 <c>?</c> 放过（那可能是真的在提问；中文语境本应使用全角？）。
/// </summary>
public static class TextIntegrity
{
    public static void EnsureNotEncodingLoss(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return; // 必填与否由各实体自己的校验负责

        var trimmed = value.Trim();
        var onlyQuestions = trimmed.Replace("?", "").Trim().Length == 0;
        var hasRun = trimmed.Contains("??", StringComparison.Ordinal);
        if (!onlyQuestions && !hasRun) return;

        throw new DomainException(
            "TEXT_ENCODING_LOSS",
            $"{field}「{trimmed}」含连续问号，像是提交端编码转换丢了字。请确认客户端使用 UTF-8，或直接在界面录入。");
    }
}
