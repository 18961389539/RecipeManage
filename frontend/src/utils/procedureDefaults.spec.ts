import { describe, expect, it } from "vitest";
import { MAX_PARAM_SLOTS, defaultOperation, defaultParameters } from "./procedureDefaults";
import type { StepType } from "../api/types";

const ALL_TYPES: StepType[] = [
  "Heat", "Hold", "Cool", "Mix", "Pressure", "Transfer", "QualityCheck", "ManualConfirm", "Wait"
];

/**
 * 缺省工艺内容是新建工步的起点：设计器、相模板添加、并行单元三个入口共用这一份。
 * 这些值会一路带到批记录与写 PLC 的参数里，所以每条都锁住，改这里就是改工艺。
 */
describe("procedureDefaults", () => {
  it("九种工步类型都有操作名，且带 OP- 前缀", () => {
    for (const type of ALL_TYPES) expect(defaultOperation(type)).toMatch(/^OP-/);
  });

  it("九种工步类型都给出至少一个参数槽，槽位从 0 连续编号", () => {
    for (const type of ALL_TYPES) {
      const params = defaultParameters(type);
      expect(params.length).toBeGreaterThan(0);
      expect(params.map((p) => p.slotIndex)).toEqual(params.map((_, i) => i));
    }
  });

  it("上位机三类不写 PLC，其余工艺相默认写 PLC", () => {
    for (const type of ["Wait", "QualityCheck", "ManualConfirm"] as StepType[])
      expect(defaultParameters(type).every((p) => !p.writeToPlc)).toBe(true);
    for (const type of ["Heat", "Hold", "Cool", "Mix", "Pressure", "Transfer"] as StepType[])
      expect(defaultParameters(type)[0].writeToPlc).toBe(true);
  });

  it("升温三槽：温度带规格且归档为质量项，斜率与时长不带", () => {
    expect(defaultParameters("Heat")).toEqual([
      { slotIndex: 0, name: "目标温度", engineeringUnit: "℃", setpoint: 530, min: 520, max: 540, writeToPlc: true, archiveAsQuality: true, scaleWithBatch: false },
      { slotIndex: 1, name: "升温斜率", engineeringUnit: "℃/min", setpoint: 8, min: 4, max: 12, writeToPlc: true, archiveAsQuality: false, scaleWithBatch: false },
      { slotIndex: 2, name: "升温时长", engineeringUnit: "s", setpoint: 8, min: 0.5, max: 3600, writeToPlc: false, archiveAsQuality: false, scaleWithBatch: false }
    ]);
  });

  it("只有转移量随批量缩放", () => {
    const scaling = ALL_TYPES
      .flatMap((t) => defaultParameters(t).filter((p) => p.scaleWithBatch).map((p) => `${t}/${p.name}`));
    expect(scaling).toEqual(["Transfer/转移量"]);
  });

  it("每次调用返回新对象：两份草稿不会共享同一组参数", () => {
    const a = defaultParameters("Hold");
    a[0].setpoint = 999;
    expect(defaultParameters("Hold")[0].setpoint).toBe(530);
  });

  it("槽上限与后端 RecipeParameter.MaxSlots 同值", () => {
    expect(MAX_PARAM_SLOTS).toBe(16);
  });
});
