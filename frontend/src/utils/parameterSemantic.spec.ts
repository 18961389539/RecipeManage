import { describe, expect, it } from "vitest";
import { measuredTagRequired, parameterSemanticLabel, parameterSemanticOptions } from "./labels";

/**
 * 参数语义下拉的契约。
 *
 * 为什么单独钉：新增的「实测值」是"非热处理工艺也能自证"的入口——流量/扭矩/厚度/pH 这类量
 * 在名称关键词推断表里永远推不出来源（推断表只有 HoldTime/Temperature/Pressure），
 * 以前推不出来的后果不是报错，而是每条批次都被判超差、要质量写一句偏差意见才能放行。
 * 所以选项少一个、或标签退化成键名，都是把那条静默失败路径放回来。
 */
describe("参数语义选项", () => {
  it("四个语义都在，顺序稳定（未声明在前，历史配方默认值）", () => {
    expect(parameterSemanticOptions).toEqual(["Unspecified", "Duration", "Rate", "MeasuredValue"]);
  });

  it("每个语义都有中文标签，不会退化成显示键名", () => {
    for (const s of parameterSemanticOptions) {
      const label = parameterSemanticLabel(s);
      expect(label).toBeTruthy();
      expect(label).not.toBe(s);
    }
    expect(parameterSemanticLabel("MeasuredValue")).toBe("实测值");
  });

  it("只有「实测值」把实测点从可选变成必填", () => {
    expect(measuredTagRequired("MeasuredValue")).toBe(true);
    // 其余语义仍然允许留空按名称推断：那是历史配方与已密封快照唯一的取数路径。
    for (const s of parameterSemanticOptions.filter((o) => o !== "MeasuredValue"))
      expect(measuredTagRequired(s)).toBe(false);
    expect(measuredTagRequired(undefined)).toBe(false);
    expect(measuredTagRequired(null)).toBe(false);
  });
});
