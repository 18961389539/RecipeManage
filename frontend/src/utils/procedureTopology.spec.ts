import { describe, expect, it } from "vitest";
import {
  canJoinSteps, invalidPlcProgram, missingIsa88Name, nextStepCode, terminalSteps
} from "./procedureTopology";
import type { TopoStep } from "./procedureTopology";
import type { StepType } from "../api/types";

function step(id: string, code: string, ordinal: number, unitProcedure: string, type: StepType = "Heat", plcProgramId?: number | null): TopoStep {
  return { id, code, ordinal, unitProcedure, type, operation: `OP-${code}`, plcProgramId };
}

/** 两条泳道各三个工步：UP-01 a1<a2<a3，UP-02 b1<b2<b3。 */
function fixture(): TopoStep[] {
  return [
    step("a1", "S10", 0, "UP-01 热处理单元"),
    step("a2", "S20", 1, "UP-01 热处理单元"),
    step("a3", "S30", 2, "UP-01 热处理单元"),
    step("b1", "S40", 3, "UP-02 镀层单元", "Mix"),
    step("b2", "S50", 4, "UP-02 镀层单元", "Mix"),
    step("b3", "S60", 5, "UP-02 镀层单元", "Mix")
  ];
}

/**
 * 这三个入口（画布拖拽连线、下拉添加连线、汇合到当前工步）用的是同一个 canJoinSteps，
 * 规则一旦在某个入口上被绕过，保存时后端会拒，用户在画布上看到的却是"连上了"。
 */
describe("procedureTopology", () => {
  it("同单元只能顺着序号往后排", () => {
    const steps = fixture();
    expect(canJoinSteps(steps, steps[0], steps[1])).toBe(true);
    expect(canJoinSteps(steps, steps[1], steps[0])).toBe(false);
    expect(canJoinSteps(steps, steps[0], steps[0])).toBe(false);
  });

  it("跨单元只能从本单元末工步进目标单元首工步", () => {
    const steps = fixture();
    expect(canJoinSteps(steps, steps[2], steps[3])).toBe(true);
    expect(canJoinSteps(steps, steps[1], steps[3])).toBe(false);
    expect(canJoinSteps(steps, steps[2], steps[4])).toBe(false);
  });

  it("泳道首末工步按 ordinal 判定，与数组顺序无关", () => {
    const steps = fixture().reverse();
    // 数组里 a3 与 b1 不相邻，但ordinal 上它们是两条泳道的尾/头，所以该连上。
    expect(canJoinSteps(steps, steps[3], steps[2])).toBe(true);
    expect(canJoinSteps(steps, steps[2], steps[3])).toBe(false);
    expect(canJoinSteps(steps, steps[0], steps[1])).toBe(false);
  });

  it("空单元规程落到缺省泳道，所以两条泳道之间算同单元", () => {
    const steps = [step("a1", "S10", 0, "  "), step("a2", "S20", 1, "")];
    expect(canJoinSteps(steps, steps[0], steps[1])).toBe(true);
    expect(canJoinSteps(steps, steps[1], steps[0])).toBe(false);
  });

  it("terminalSteps 取的是没有后继的工步，并排除汇合目标自己", () => {
    const steps = fixture();
    const edges = [
      { fromStepId: "a1", toStepId: "a2" },
      { fromStepId: "a2", toStepId: "a3" },
      { fromStepId: "b1", toStepId: "b2" }
    ];
    expect(terminalSteps(steps, edges, "b3").map((s) => s.id)).toEqual(["a3", "b2"]);
    expect(terminalSteps(steps, edges, "a3").map((s) => s.id)).toEqual(["b2", "b3"]);
  });

  it("缺单元规程或操作名时给出带工步码的文案，齐全时放行", () => {
    const steps = fixture();
    expect(missingIsa88Name(steps)).toBeNull();
    expect(missingIsa88Name([{ ...steps[1], unitProcedure: "  " }])).toBe("工步 S20 必须填写单元规程");
    expect(missingIsa88Name([{ ...steps[2], operation: "" }])).toBe("工步 S30 必须填写操作");
  });

  it("程序号 7 / 8 是上位机工步占用的，工艺相不能选；缺省值按类型回退", () => {
    const steps = fixture();
    expect(invalidPlcProgram(steps)).toBeNull();
    expect(invalidPlcProgram([{ ...steps[0], plcProgramId: 7 }])).toBe(
      "工步 S10 的 PLC 程序号 7 无效（写 PLC 用 1–6 或 9–99）"
    );
    expect(invalidPlcProgram([{ ...steps[3], type: "Transfer", plcProgramId: 100 }])).toContain("程序号 100");
    // Wait 的缺省程序号是 0，但它是上位机工步，不参与这项校验。
    expect(invalidPlcProgram([{ ...steps[0], type: "Wait", plcProgramId: null }])).toBeNull();
  });

  it("新工步编码按现有数量续号，删过的号不复用", () => {
    expect(nextStepCode(0)).toBe("S10");
    expect(nextStepCode(3)).toBe("S40");
  });
});
