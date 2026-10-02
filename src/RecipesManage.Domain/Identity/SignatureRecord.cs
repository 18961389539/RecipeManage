using RecipesManage.Domain.Common;

namespace RecipesManage.Domain.Identity;

/// <summary>
/// 一次电子签名的结构化记录：谁、在什么时候、对哪个对象、签了哪一句话。
///
/// 为什么不再只靠审计日志：以前批次/化验的签名只是 audit_logs 里一行
/// "含义 + 附加说明" 的拼接文本，读的时候用当前代码里的含义表去反查再剥前缀。
/// 后果是——改一次措辞，历史批记录里展示的就不再是当时签名人确认的那句话；
/// 21 CFR 11.50 要求签名附带的是"签署当时"的含义。
///
/// 现在 <see cref="Meaning"/> 在签署时冻结成文本落库，之后只读不改（没有任何改写方法）；
/// 展示、PDF、审计一律读这里，不再回头查代码里的含义表。
/// 同一次签名仍会在 audit_logs 写一行，保持审计履历完整——两者 Id 不同，但动作与对象相同。
/// </summary>
public sealed class SignatureRecord : Entity
{
    public Guid? UserId { get; private set; }

    /// <summary>签署人显示名，签署当时的快照（账号之后改名、停用都不影响这条记录）。</summary>
    public string SignerName { get; private set; } = string.Empty;

    /// <summary>动作键，如 <c>batch.release.esign</c>。</summary>
    public string Action { get; private set; } = string.Empty;

    /// <summary>被签对象的类型与标识，如 <c>ProductionBatch</c> / 批次 Id。</summary>
    public string EntityType { get; private set; } = string.Empty;

    public string EntityId { get; private set; } = string.Empty;

    /// <summary>签署当时向签名人展示并由其确认的含义原文。</summary>
    public string Meaning { get; private set; } = string.Empty;

    /// <summary>签名人补充的原因 / 备注（不含含义原文）。</summary>
    public string? Detail { get; private set; }

    public DateTimeOffset SignedAt { get; private set; } = DateTimeOffset.UtcNow;

    private SignatureRecord() { }

    public SignatureRecord(
        Guid? userId, string signerName, string action, string entityType, string entityId, string meaning, string? detail)
    {
        if (string.IsNullOrWhiteSpace(meaning))
            throw new DomainException("ESIGN", "电子签名必须带有签署含义。");
        if (string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(entityType) || string.IsNullOrWhiteSpace(entityId))
            throw new DomainException("ESIGN", "电子签名必须绑定到具体对象。");

        UserId = userId;
        SignerName = signerName;
        Action = action;
        EntityType = entityType;
        EntityId = entityId;
        Meaning = meaning;
        Detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim();
    }
}
