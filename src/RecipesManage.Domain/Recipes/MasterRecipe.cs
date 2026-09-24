using RecipesManage.Domain.Common;

namespace RecipesManage.Domain.Recipes;

public sealed class MasterRecipe : Entity
{
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string ProductCode { get; private set; } = string.Empty;
    public string ProductName { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public RecipeLifecycle Lifecycle { get; private set; } = RecipeLifecycle.Active;
    public Guid? CurrentDraftVersionId { get; private set; }
    public Guid? CurrentApprovedVersionId { get; private set; }
    /// <summary>本配方走哪条审批链；null = 走默认链。提交时解析并冻结，改这里只影响之后的提交。</summary>
    public string? ApprovalChainCode { get; private set; }

    public List<RecipeVersion> Versions { get; private set; } = [];

    private MasterRecipe() { }

    public static MasterRecipe Create(
        string code,
        string name,
        string productCode,
        string productName,
        string? description,
        Guid createdBy)
    {
        // 创建与导入都走这里，所以一处就能挡住编码丢失的名称落库（见 TextIntegrity）。
        TextIntegrity.EnsureNotEncodingLoss(code, "配方编码");
        TextIntegrity.EnsureNotEncodingLoss(name, "配方名称");
        TextIntegrity.EnsureNotEncodingLoss(productCode, "产品编码");
        TextIntegrity.EnsureNotEncodingLoss(productName, "产品名称");

        var recipe = new MasterRecipe
        {
            Code = code.Trim().ToUpperInvariant(),
            Name = name.Trim(),
            ProductCode = productCode.Trim(),
            ProductName = productName.Trim(),
            Description = description
        };

        var v1 = RecipeVersion.CreateDraft(recipe.Id, 1, createdBy, "初始版本");
        recipe.Versions.Add(v1);
        recipe.CurrentDraftVersionId = v1.Id;
        return recipe;
    }

    public RecipeVersion RequireDraft()
    {
        var draft = Versions.SingleOrDefault(v => v.Id == CurrentDraftVersionId)
                    ?? Versions.SingleOrDefault(v => v.Status == RecipeStatus.Draft);
        if (draft is null)
            throw new DomainException("NO_DRAFT", "当前没有可编辑的草稿版本，请先从已批准版本创建新版本。");
        return draft;
    }

    public RecipeVersion CreateNextDraft(Guid createdBy, string changeNote)
    {
        if (Versions.Any(v => v.Status is RecipeStatus.Draft or RecipeStatus.InReview))
            throw new DomainException("DRAFT_EXISTS", "已存在草稿或审核中的版本，不能并行开版。");

        var approved = Versions.SingleOrDefault(v => v.Id == CurrentApprovedVersionId)
                       ?? throw new DomainException("NO_APPROVED", "没有已批准版本可供升版。");

        var next = approved.CloneAsDraft(Versions.Max(v => v.VersionNumber) + 1, createdBy, changeNote);
        Versions.Add(next);
        CurrentDraftVersionId = next.Id;
        Touch();
        return next;
    }

    public void MarkApproved(RecipeVersion version)
    {
        if (CurrentApprovedVersionId is Guid previousId)
        {
            var previous = Versions.Single(v => v.Id == previousId);
            if (previous.Status == RecipeStatus.Approved)
                previous.MarkObsolete();
        }

        CurrentApprovedVersionId = version.Id;
        if (CurrentDraftVersionId == version.Id)
            CurrentDraftVersionId = null;
        Touch();
    }

    public void RestoreDraft(RecipeVersion version)
    {
        if (version.MasterRecipeId != Id)
            throw new DomainException("VERSION_MISMATCH", "版本不属于该配方。");
        CurrentDraftVersionId = version.Id;
        Touch();
    }

    public void UpdateHeader(string name, string productCode, string productName, string? description)
    {
        Name = name.Trim();
        ProductCode = productCode.Trim();
        ProductName = productName.Trim();
        Description = description;
        Touch();
    }

    /// <summary>
    /// 指定本配方以后走哪条审批链（null = 默认链）。
    /// 只影响之后的提交：在审版本的节点早在提交时就冻结好了，改这里动不了它。
    /// </summary>
    public void UseApprovalChain(string? chainCode)
    {
        ApprovalChainCode = string.IsNullOrWhiteSpace(chainCode) ? null : chainCode.Trim();
        Touch();
    }
}
