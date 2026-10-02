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

/// <summary>
/// 调度只认的四类执行语义。工艺相名称与 PLC 程序号不在本枚举上扩展。
/// </summary>
public enum ExecutionKind
{
    WritePlc = 0,
    Wait = 1,
    QualityCheck = 2,
    ManualConfirm = 3
}

/// <summary>
/// 工步类型：上位机三种执行类，以及写 PLC 时的默认程序号别名。
/// 写 PLC 时若未指定 <c>PlcProgramId</c>，则把本枚举整型写入点表 Step_Type。
/// 自定义工艺相（冲洗、气缸等）仍选写 PLC 的一种别名，另填程序号 9–99。
/// 调度分支请用 <see cref="PlcProgram.Kind"/>，不要再按升温/搅拌分叉。
/// </summary>
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

/// <summary>
/// 参数语义。声明后由它决定归档取哪个实测点、哪个参数是工艺时长，
/// 不再依赖名称里的中文关键词——那是历史数据的回退路径。
/// 新值只追加在末尾：库里存的是 int，历史参数行不回填。
/// </summary>
public enum ParameterSemantic
{
    Unspecified = 0,
    Duration = 1,
    Rate = 2,
    /// <summary>
    /// 这条参数是<strong>被测出来的质量特性</strong>（流量、pH、扭矩、厚度、粘度…），
    /// 不是设定值也不是时长。声明它就必须同时声明 <c>MeasuredTag</c>：
    /// 名字里没有任何可推断关键词，靠猜的结果是"归档不到值 → 判超差 → 质量随手写一句意见就放行"。
    /// </summary>
    MeasuredValue = 3
}

public enum ApprovalDecision
{
    Pending = 0,
    Approved = 1,
    Rejected = 2
}
