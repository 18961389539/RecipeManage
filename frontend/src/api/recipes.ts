import http from "./http";
import type { RecipeDetailDto, RecipeVersionDto, RecipeVersionDiffDto, EdgeDto } from "./types";

/**
 * 配方域的接口收口（返回解包后的 data，失败一律 reject 交给调用方呈现）。
 *
 * 工艺保存/提交/审核/重开/升版都要带口令：统一走 `password` 字段的请求体，
 * 一律 POST/PUT，绝不把口令放进 URL（会被代理与访问日志留下明文）。
 */

export async function getRecipeDetail(id: string): Promise<RecipeDetailDto> {
  return (await http.get<RecipeDetailDto>(`/recipes/${id}`)).data;
}

export async function updateRecipeHeader(
  id: string,
  body: { name: string; productCode: string; productName: string; description: string | null }
): Promise<void> {
  await http.put(`/recipes/${id}`, body);
}

export async function saveProcedure(
  id: string,
  body: { steps: RecipeVersionDto["steps"]; edges: Pick<EdgeDto, "fromStepId" | "toStepId">[]; password: string; changeReason?: string | null }
): Promise<void> {
  await http.put(`/recipes/${id}/procedure`, body);
}

export async function submitRecipe(id: string, password: string): Promise<void> {
  await http.post(`/recipes/${id}/submit`, { password });
}

/** 指定本配方走哪条审批链；`code` 传 null 表示改回默认链。只影响之后的提交。 */
export async function useApprovalChain(id: string, code: string | null, password: string): Promise<void> {
  await http.put(`/recipes/${id}/approval-chain`, { code, password });
}

export async function decideRecipe(
  id: string,
  body: { decision: "Approved" | "Rejected"; comment: string; password: string }
): Promise<void> {
  await http.post(`/recipes/${id}/decide`, body);
}

export async function reopenRecipe(id: string, password: string): Promise<void> {
  await http.post(`/recipes/${id}/reopen`, { password });
}

export async function createRecipeVersion(id: string, changeNote: string, password: string): Promise<void> {
  await http.post(`/recipes/${id}/new-version`, { changeNote, password });
}

export async function compareVersions(
  id: string,
  fromVersion: number,
  toVersion: number
): Promise<RecipeVersionDiffDto> {
  return (await http.get<RecipeVersionDiffDto>(`/recipes/${id}/compare`, {
    params: { fromVersion, toVersion }
  })).data;
}
