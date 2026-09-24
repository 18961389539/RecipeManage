using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RecipesManage.Application.Dtos;
using RecipesManage.Application.Services;

namespace RecipesManage.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/recipes")]
public sealed class RecipesController(RecipeService recipes) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<RecipeListItemDto>> List(CancellationToken ct) => recipes.ListAsync(ct);

    [HttpGet("{id:guid}")]
    public Task<RecipeDetailDto> Get(Guid id, CancellationToken ct) => recipes.GetAsync(id, ct);

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.RecipeAuthor)]
    public Task<RecipeDetailDto> Create(CreateRecipeRequest request, CancellationToken ct) =>
        recipes.CreateAsync(request, ct);

    [HttpPut("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RecipeAuthor)]
    public Task<RecipeDetailDto> Update(Guid id, UpdateRecipeRequest request, CancellationToken ct) =>
        recipes.UpdateHeaderAsync(id, request, ct);

    [HttpPut("{id:guid}/procedure")]
    [Authorize(Policy = AuthorizationPolicies.RecipeAuthor)]
    public Task<RecipeDetailDto> SaveProcedure(Guid id, SaveProcedureRequest request, CancellationToken ct) =>
        recipes.SaveProcedureAsync(id, request, ct);

    [HttpPost("{id:guid}/submit")]
    [Authorize(Policy = AuthorizationPolicies.RecipeAuthor)]
    public Task<RecipeDetailDto> Submit(Guid id, SubmitRecipeRequest request, CancellationToken ct) =>
        recipes.SubmitAsync(id, request, ct);

    // decide / reopen 的合法角色取决于"当前停在哪个审核节点"，静态策略表达不了，
    // 仍由 RecipeService 按节点判定（含职责分离：同一人不得担任本版本多个节点）。
    [HttpPost("{id:guid}/decide")]
    public Task<RecipeDetailDto> Decide(Guid id, DecideRequest request, CancellationToken ct) =>
        recipes.DecideAsync(id, request, ct);

    [HttpPost("{id:guid}/reopen")]
    [Authorize(Policy = AuthorizationPolicies.RecipeAuthor)]
    public Task<RecipeDetailDto> Reopen(Guid id, [FromBody] SubmitRecipeRequest request, CancellationToken ct) =>
        recipes.ReopenAsync(id, request, ct);

    /// <summary>
    /// 指定本配方走哪条审批链。链由 Admin 配，选哪条由工艺工程师定，
    /// 所以这里走 RecipeAuthor 而不是 Admin——和在提交单上署名的是同一批人。
    /// </summary>
    [HttpPut("{id:guid}/approval-chain")]
    [Authorize(Policy = AuthorizationPolicies.RecipeAuthor)]
    public Task<RecipeDetailDto> SetApprovalChain(Guid id, UseApprovalChainRequest request, CancellationToken ct) =>
        recipes.SetApprovalChainAsync(id, request, ct);

    [HttpPost("{id:guid}/new-version")]
    [Authorize(Policy = AuthorizationPolicies.RecipeAuthor)]
    public Task<RecipeDetailDto> NewVersion(Guid id, NewVersionRequest request, CancellationToken ct) =>
        recipes.NewVersionAsync(id, request, ct);

    [HttpGet("{id:guid}/compare")]
    public Task<RecipeVersionDiffDto> Compare(Guid id, [FromQuery] int fromVersion, [FromQuery] int toVersion, CancellationToken ct) =>
        recipes.CompareAsync(id, fromVersion, toVersion, ct);

    [HttpGet("export")]
    [Authorize(Policy = AuthorizationPolicies.RecipeExport)]
    public Task<RecipePackageDto> Export(CancellationToken ct) => recipes.ExportAsync(ct);

    [HttpPost("import")]
    [Authorize(Policy = AuthorizationPolicies.RecipeAuthor)]
    public Task<RecipeImportResultDto> Import(RecipePackageDto package, CancellationToken ct) =>
        recipes.ImportAsync(package, ct);
}
