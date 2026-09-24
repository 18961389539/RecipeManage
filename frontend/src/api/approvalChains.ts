import http from "./http";
import type { ApprovalChainDto, ApprovalChainStepRequest } from "./types";

/**
 * 审批链配置的接口收口。
 *
 * 读接口对全体登录用户开放（审核台要能解释"这一版为什么轮到某个角色"），
 * 写接口只有 Admin 过得了后端策略；这里不做前端权限判断，失败一律 reject 由调用方呈现。
 */

export async function listApprovalChains(): Promise<ApprovalChainDto[]> {
  return (await http.get<ApprovalChainDto[]>("/approval-chains")).data;
}

/** `id` 为空即新建；保存是整条链覆盖写，节点没有单独增删接口。 */
export async function saveApprovalChain(body: {
  id: string | null;
  code: string;
  name: string;
  isDefault: boolean;
  enabled: boolean;
  steps: ApprovalChainStepRequest[];
  password: string;
}): Promise<ApprovalChainDto> {
  return (await http.post<ApprovalChainDto>("/approval-chains", body)).data;
}

export async function deleteApprovalChain(id: string, password: string): Promise<void> {
  await http.delete(`/approval-chains/${id}`, { data: { password } });
}
