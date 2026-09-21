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
    public Task<RecipeDetailDto> Create(CreateRecipeRequest request, CancellationToken ct) =>
        recipes.CreateAsync(request, ct);

    [HttpPut("{id:guid}")]
    public Task<RecipeDetailDto> Update(Guid id, UpdateRecipeRequest request, CancellationToken ct) =>
        recipes.UpdateHeaderAsync(id, request, ct);

    [HttpPut("{id:guid}/procedure")]
    public Task<RecipeDetailDto> SaveProcedure(Guid id, SaveProcedureRequest request, CancellationToken ct) =>
        recipes.SaveProcedureAsync(id, request, ct);

    [HttpPost("{id:guid}/submit")]
    public Task<RecipeDetailDto> Submit(Guid id, SubmitRecipeRequest request, CancellationToken ct) =>
        recipes.SubmitAsync(id, request, ct);

    [HttpPost("{id:guid}/decide")]
    public Task<RecipeDetailDto> Decide(Guid id, DecideRequest request, CancellationToken ct) =>
        recipes.DecideAsync(id, request, ct);

    [HttpPost("{id:guid}/reopen")]
    public Task<RecipeDetailDto> Reopen(Guid id, [FromBody] SubmitRecipeRequest request, CancellationToken ct) =>
        recipes.ReopenAsync(id, request, ct);

    [HttpPost("{id:guid}/new-version")]
    public Task<RecipeDetailDto> NewVersion(Guid id, NewVersionRequest request, CancellationToken ct) =>
        recipes.NewVersionAsync(id, request, ct);

    [HttpGet("{id:guid}/compare")]
    public Task<RecipeVersionDiffDto> Compare(Guid id, [FromQuery] int fromVersion, [FromQuery] int toVersion, CancellationToken ct) =>
        recipes.CompareAsync(id, fromVersion, toVersion, ct);

    [HttpGet("export")]
    public Task<RecipePackageDto> Export(CancellationToken ct) => recipes.ExportAsync(ct);

    [HttpPost("import")]
    public Task<RecipeImportResultDto> Import(RecipePackageDto package, CancellationToken ct) =>
        recipes.ImportAsync(package, ct);
}
