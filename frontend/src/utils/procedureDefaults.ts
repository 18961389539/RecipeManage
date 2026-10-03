import type { ParameterDto, StepType } from "../api/types";

/**
 * 新建工步时的缺省操作名与参数槽（纯数据）。
 *
 * 从设计器里搬出来的原因不是"太长"，而是这套默认值会被三个入口共用：
 * 上位机按钮、相模板添加、并行单元首工步；以前只有第一个入口用得到它，
 * 另两个各自抄了一份。参数是工艺内容的起点，改错了会一路带到批记录里，所以配了单测。
 */

const operationByType: Record<StepType, string> = {
  Heat: "OP-Heat 升温",
  Hold: "OP-Hold 保温",
  Cool: "OP-Cool 冷却",
  Mix: "OP-Mix 搅拌",
  Pressure: "OP-Press 加压",
  Transfer: "OP-Xfer 转移",
  QualityCheck: "OP-QC 质检",
  ManualConfirm: "OP-Manual 人工确认",
  Wait: "OP-Wait 等待"
};

export function defaultOperation(type: StepType): string {
  return operationByType[type];
}

function param(
  slotIndex: number,
  name: string,
  engineeringUnit: string,
  setpoint: number,
  min: number | null = null,
  max: number | null = null,
  writeToPlc = true,
  archiveAsQuality = false,
  scaleWithBatch = false
): ParameterDto {
  return { slotIndex, name, engineeringUnit, setpoint, min, max, writeToPlc, archiveAsQuality, scaleWithBatch };
}

export function defaultParameters(type: StepType): ParameterDto[] {
  switch (type) {
    // 上位机三类不写 PLC：确认意见 / 质检项 / 等待时长。
    // 质检项的硬度是实验室量，archiveAsQuality 必须为 false——标了归档又在 PLC 上没有
    // 实测来源，提交审核时会被 QUALITY_SOURCE 拦下（实验室指标走质检样品 LIMS）。
    case "ManualConfirm": return [param(0, "确认意见", "", 0, null, null, false, false)];
    case "QualityCheck": return [param(0, "硬度", "HB", 95, 90, 110, false, false)];
    case "Wait": return [param(0, "等待时长", "s", 5, 0.5, 3600, false, false)];
    case "Heat": return [
      param(0, "目标温度", "℃", 530, 520, 540, true, true),
      param(1, "升温斜率", "℃/min", 8, 4, 12),
      param(2, "升温时长", "s", 8, 0.5, 3600, false, false)
    ];
    case "Hold": return [
      param(0, "保温温度", "℃", 530, 525, 535, true, true),
      param(1, "保温时长", "s", 8, 1, 3600, true, true)
    ];
    case "Cool": return [
      param(0, "终点温度", "℃", 40, 20, 60, true, true),
      param(1, "冷却时长", "s", 6, 1, 600)
    ];
    case "Mix": return [
      param(0, "搅拌转速", "rpm", 60, 10, 200),
      param(1, "搅拌时长", "s", 8, 1, 600, true, true)
    ];
    case "Pressure": return [
      param(0, "目标压力", "bar", 2.5, 1, 6, true, true),
      param(1, "保压时长", "s", 6, 1, 600)
    ];
    case "Transfer": return [
      param(0, "转移量", "kg", 50, 1, 500, true, true, true),
      param(1, "转移时长", "s", 5, 1, 300)
    ];
    default: return [param(0, "设定值", "", 0)];
  }
}

/** 参数槽上限，与后端 RecipeParameter.MaxSlots 同值。 */
export const MAX_PARAM_SLOTS = 16;
