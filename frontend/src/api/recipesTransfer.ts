import { ElMessage } from "element-plus";
import { t } from "../i18n";
import http from "./http";
import type { RecipeImportResultDto, RecipePackageDto } from "./types";
import { formatFileDate } from "../utils/format";

/**
 * 配方包的导出与导入。配方列表和"用户与系统管理"两个入口共用这一份实现。
 *
 * 之前两处各写了一遍，其中配方列表把后端返回的 messages 丢掉了——导入被跳过的原因
 * 只剩一个"跳过 N"的数字，现场无从下手。失败提示也收在这里：调用方只负责 busy 和刷新。
 */

export async function exportRecipePackage(): Promise<boolean> {
  try {
    const { data } = await http.get<RecipePackageDto>("/recipes/export");
    const blob = new Blob([JSON.stringify(data, null, 2)], { type: "application/json;charset=utf-8" });
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = `brmes-recipes-${formatFileDate()}.json`;
    a.click();
    URL.revokeObjectURL(a.href);
    return true;
  } catch (e) {
    ElMessage.error((e as Error).message);
    return false;
  }
}

/** @returns 导入成功（含"全部已存在所以一条没建"）时为 true，调用方据此刷新列表。 */
export async function importRecipePackage(file: File): Promise<boolean> {
  let pkg: RecipePackageDto;
  try {
    pkg = JSON.parse(await file.text()) as RecipePackageDto;
  } catch {
    ElMessage.error(t("{0} 不是可读的 JSON，请选择导出的 .json 配方包。", file.name));
    return false;
  }

  if (!Array.isArray(pkg?.recipes)) {
    ElMessage.error(t("{0} 里没有 recipes 数组，不像是本系统导出的配方包。", file.name));
    return false;
  }

  try {
    const { data } = await http.post<RecipeImportResultDto>("/recipes/import", pkg);
    ElMessage.success(t("导入完成：新建 {0}，跳过 {1}", data.created, data.skipped));
    if (data.messages.length) ElMessage.info(data.messages.slice(0, 3).join("；"));
    return true;
  } catch (e) {
    ElMessage.error((e as Error).message);
    return false;
  }
}
