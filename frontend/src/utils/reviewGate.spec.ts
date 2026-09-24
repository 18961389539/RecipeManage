import { describe, expect, it } from "vitest";
import { canDecideReview, headPendingNode } from "./reviewGate";
import type { ApprovalDto, RecipeVersionDto, UserRole } from "../api/types";

/**
 * 审核决定权的闸门。
 *
 * 审批链是数据之后，界面不能再认"Supervisor / Quality"这两个名字——
 * 那样新配的节点（比如 Admin 出厂放行）会永远灰着，而且没人会报错。
 * 这两条同时钉住"取头节点要看 seq"：整条链提交时就一次展开完，Pending 有好几条是常态。
 */

function node(partial: Partial<ApprovalDto> & { seq: number }): ApprovalDto {
  return {
    id: `n${partial.seq}`,
    node: "Supervisor",
    title: `节点${partial.seq}`,
    requiredRole: "Supervisor",
    decision: "Pending",
    ...partial,
    seq: partial.seq
  } as ApprovalDto;
}

const version = (status: string, approvals: ApprovalDto[]) =>
  ({ status, approvals }) as RecipeVersionDto;

describe("审批闸门", () => {
  it("取头节点按 seq 比，不看数组顺序", () => {
    const outOfOrder = version("InReview", [node({ seq: 3, requiredRole: "Admin" }), node({ seq: 1 }), node({ seq: 2, requiredRole: "Quality" })]);
    expect(headPendingNode(outOfOrder)?.seq).toBe(1);
    expect(headPendingNode(version("InReview", []))).toBeNull();
    expect(headPendingNode(null)).toBeNull();
  });

  it("已签掉的节点不算头节点", () => {
    const v = version("InReview", [
      node({ seq: 1, decision: "Approved" }),
      node({ seq: 2, requiredRole: "Quality" }),
      node({ seq: 3, requiredRole: "Admin" })
    ]);
    expect(headPendingNode(v)?.seq).toBe(2);
  });

  it("只有头节点要求的角色能签，包括链上新出现的角色", () => {
    const v = version("InReview", [node({ seq: 1 }), node({ seq: 2, requiredRole: "Quality" })]);
    expect(canDecideReview(v, "Supervisor")).toBe(true);
    expect(canDecideReview(v, "Quality")).toBe(false);
    expect(canDecideReview(v, "Admin")).toBe(false);
    expect(canDecideReview(v, undefined)).toBe(false);

    // 头节点换成 Admin（老实现只认 Supervisor/Quality 两个名字，这里就会永远灰着）
    const released = version("InReview", [node({ seq: 1, decision: "Approved" }), node({ seq: 2, requiredRole: "Admin" })]);
    expect(canDecideReview(released, "Admin")).toBe(true);
    expect(canDecideReview(released, "Quality")).toBe(false);
  });

  it("不在审就没有决定权，哪怕角色对得上", () => {
    const v = version("Approved", [node({ seq: 1, decision: "Approved" })]);
    expect(canDecideReview(v, "Supervisor")).toBe(false);
    const draft = version("Draft", [node({ seq: 1 })]);
    // 草稿没有已展开的链，界面按"无可签节点"处理。
    expect(canDecideReview(draft, "Supervisor")).toBe(false);
  });

  it("提交动作那一签不给决定权（它随提交就已经签掉了）", () => {
    const v = version("InReview", [
      node({ seq: 0, node: "Submission", title: "提交人", requiredRole: "ProcessEngineer" as UserRole, decision: "Approved" }),
      node({ seq: 1 })
    ]);
    expect(headPendingNode(v)?.seq).toBe(1);
    expect(canDecideReview(v, "ProcessEngineer")).toBe(false);
  });
});
