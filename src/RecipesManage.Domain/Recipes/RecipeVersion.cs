using RecipesManage.Domain.Common;

namespace RecipesManage.Domain.Recipes;

public sealed class RecipeVersion : Entity
{
    public Guid MasterRecipeId { get; private set; }
    public int VersionNumber { get; private set; }
    public RecipeStatus Status { get; private set; } = RecipeStatus.Draft;
    public string? ChangeNote { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset? SubmittedAt { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }

    public List<RecipeStep> Steps { get; private set; } = [];
    public List<RecipeEdge> Edges { get; private set; } = [];
    public List<ApprovalRecord> Approvals { get; private set; } = [];

    private RecipeVersion() { }

    internal static RecipeVersion CreateDraft(Guid masterRecipeId, int versionNumber, Guid createdBy, string? changeNote)
    {
        return new RecipeVersion
        {
            MasterRecipeId = masterRecipeId,
            VersionNumber = versionNumber,
            CreatedBy = createdBy,
            ChangeNote = changeNote,
            Status = RecipeStatus.Draft
        };
    }

    internal RecipeVersion CloneAsDraft(int versionNumber, Guid createdBy, string changeNote)
    {
        var clone = CreateDraft(MasterRecipeId, versionNumber, createdBy, changeNote);
        var idMap = new Dictionary<Guid, Guid>();

        foreach (var step in Steps.OrderBy(s => s.Ordinal))
        {
            var copied = step.CloneTo(clone.Id);
            idMap[step.Id] = copied.Id;
            clone.Steps.Add(copied);
        }

        foreach (var edge in Edges)
        {
            clone.Edges.Add(new RecipeEdge(clone.Id, idMap[edge.FromStepId], idMap[edge.ToStepId]));
        }

        return clone;
    }

    public void EnsureDraft()
    {
        if (Status != RecipeStatus.Draft)
            throw new DomainException("NOT_DRAFT", $"版本 v{VersionNumber} 状态为 {Status}，禁止修改工艺内容。");
    }

    public void ReplaceProcedure(IEnumerable<RecipeStep> steps, IEnumerable<RecipeEdge> edges)
    {
        EnsureDraft();
        Steps.Clear();
        Edges.Clear();
        Steps.AddRange(steps);
        Edges.AddRange(edges);
        RecipeTopology.Validate(Steps, Edges);
        Touch();
    }

    public void Submit(DateTimeOffset now, Guid authorId = default, string authorName = "system", string? comment = null)
    {
        EnsureDraft();
        RecipeTopology.Validate(Steps, Edges);
        if (Steps.Count == 0)
            throw new DomainException("EMPTY_PROCEDURE", "工步为空，不能提交审核。");

        Status = RecipeStatus.InReview;
        SubmittedAt = now;
        Approvals.Clear();
        var author = ApprovalRecord.Open(Id, ApprovalLevel.Author);
        author.Complete(
            authorId,
            string.IsNullOrWhiteSpace(authorName) ? "system" : authorName,
            ApprovalDecision.Approved,
            comment,
            now);
        Approvals.Add(author);
        Approvals.Add(ApprovalRecord.Open(Id, ApprovalLevel.Supervisor));
        Touch();
    }

    public void Decide(ApprovalLevel level, Guid reviewerId, string reviewerName, ApprovalDecision decision, string? comment, DateTimeOffset now)
    {
        if (Status != RecipeStatus.InReview)
            throw new DomainException("NOT_IN_REVIEW", "当前版本不在审核中。");

        var record = Approvals.SingleOrDefault(a => a.Level == level && a.Decision == ApprovalDecision.Pending)
                     ?? throw new DomainException("NO_PENDING_LEVEL", $"没有待处理的 {level} 审核节点。");

        // 职责分离（GMP）：提交人不得审批自己的提交，同一人不得担任同一版本的多个审核节点。
        // 放在域层而非服务层，Admin 的角色旁路也无法绕过。
        if (reviewerId != Guid.Empty)
        {
            if (Approvals.Any(a => a.ReviewerId == reviewerId))
                throw new DomainException(
                    "SEGREGATION_OF_DUTIES",
                    "职责分离：该用户已在此版本的提交或审核链条中署名，不能继续担任本审核节点。");
        }

        if (decision == ApprovalDecision.Rejected)
        {
            record.Complete(reviewerId, reviewerName, decision, comment, now);
            Status = RecipeStatus.Rejected;
            Touch();
            return;
        }

        record.Complete(reviewerId, reviewerName, ApprovalDecision.Approved, comment, now);

        if (level == ApprovalLevel.Supervisor)
        {
            Approvals.Add(ApprovalRecord.Open(Id, ApprovalLevel.Quality));
        }
        else if (level == ApprovalLevel.Quality)
        {
            Status = RecipeStatus.Approved;
            ApprovedAt = now;
        }

        Touch();
    }

    public void ReopenRejected()
    {
        if (Status != RecipeStatus.Rejected)
            throw new DomainException("NOT_REJECTED", "只有被驳回的版本可以重新打开为草稿。");
        Status = RecipeStatus.Draft;
        Approvals.Clear();
        Touch();
    }

    internal void MarkObsolete()
    {
        if (Status == RecipeStatus.Approved)
            Status = RecipeStatus.Obsolete;
        Touch();
    }
}
