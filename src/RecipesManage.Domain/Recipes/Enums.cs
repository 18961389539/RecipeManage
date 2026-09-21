namespace RecipesManage.Domain.Recipes;

public enum RecipeLifecycle
{
    Active = 0,
    Obsolete = 1
}

public enum RecipeStatus
{
    Draft = 0,
    InReview = 1,
    Approved = 2,
    Rejected = 3,
    Obsolete = 4
}

/// <summary>工步类型，写入 PLC 的 Step_Type 枚举。</summary>
public enum StepType
{
    Wait = 0,
    Heat = 1,
    Hold = 2,
    Cool = 3,
    Mix = 4,
    Pressure = 5,
    Transfer = 6,
    QualityCheck = 7,
    ManualConfirm = 8
}

public enum ApprovalLevel
{
    Author = 0,
    Supervisor = 1,
    Quality = 2
}

public enum ApprovalDecision
{
    Pending = 0,
    Approved = 1,
    Rejected = 2
}
