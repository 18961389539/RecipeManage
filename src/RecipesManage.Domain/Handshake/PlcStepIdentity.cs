using System.Text.RegularExpressions;

namespace RecipesManage.Domain.Handshake;

/// <summary>
/// 写入 PLC 的 Step_ID：优先使用配方工步编码中的稳定数字（如 S10→10），避免仅按拓扑序号重编号。
/// </summary>
public static class PlcStepIdentity
{
    private static readonly Regex TrailingDigits = new(@"(\d+)\s*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex StepPrefix = new(@"[Ss](\d+)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static int FromCode(string? code, int ordinal)
    {
        if (!string.IsNullOrWhiteSpace(code))
        {
            if (TryParse(TrailingDigits.Match(code), out var trailing))
                return trailing;
            if (TryParse(StepPrefix.Match(code), out var prefixed))
                return prefixed;
        }

        return Math.Max(1, ordinal + 1);
    }

    private static bool TryParse(Match match, out int value)
    {
        value = 0;
        return match.Success
               && int.TryParse(match.Groups[1].Value, out value)
               && value is > 0 and < 1_000_000;
    }
}
