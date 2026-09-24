import type { ApprovalDto, RecipeVersionDto, UserRole } from "../api/types";

/**
 * 链上第一个还没签的节点。
 * 整条链在提交时就一次展开完，所以"后面还有 Pending"是常态——取头节点必须按 seq 比，
 * 不能拿数组里第一个 Pending 碰运气（列表顺序由后端排，改了排序就会指错节点）。
 */
export function headPendingNode(
  version: Pick<RecipeVersionDto, "approvals"> | null | undefined
): ApprovalDto | null {
  return (version?.approvals ?? [])
    .filter((a) => a.decision === "Pending")
    .reduce<ApprovalDto | null>((min, a) => (min === null || a.seq < min.seq ? a : min), null);
}

/**
 * 审核决定权的唯一口径：版本处于 InReview，且登录者的角色正是**当前待决节点要求的那个角色**。
 *
 * 节点要求谁签是提交时冻结在记录上的（`requiredRole`），不再由代码认 Supervisor/Quality 两级——
 * 审批链现在是可配的数据，认死名字就会让新配的节点在界面上永远灰着。
 * 提交人那一签由"提交审核"那一步签掉，不算决定权，所以它不会是待决节点。
 *
 * 配方设计器看草稿、审核台看在审版本，两边挑的版本不同，但闸门必须一样——
 * 此前是两份复制的 computed，改一边就会忘另一边。
 */
export function canDecideReview(
  version: Pick<RecipeVersionDto, "status" | "approvals"> | null | undefined,
  role: UserRole | undefined
): boolean {
  if (!version || version.status !== "InReview") return false;
  return !!role && headPendingNode(version)?.requiredRole === role;
}
