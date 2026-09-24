import type { PhaseParameterDto, PhaseTemplateDto, StepType } from "../api/types";

/** 写 PLC 的相类型（等待 / 质检 / 人工确认是上位机执行，不进相库）。 */
export const PROCESS_TYPES: StepType[] = ["Heat", "Hold", "Cool", "Mix", "Pressure", "Transfer"];

/** 各类型的缺省程序号；用户可以在对话框里改成 9–99 的自定义号。 */
export const DEFAULT_PROGRAM: Record<string, number> = {
  Heat: 1, Hold: 2, Cool: 3, Mix: 4, Pressure: 5, Transfer: 6
};

export function templateProgram(row: PhaseTemplateDto): number {
  return row.plcProgramId ?? DEFAULT_PROGRAM[row.stepType] ?? 1;
}

/**
 * 新建相模板时按类型预填的参数槽。
 * 刻意与后端 `DataFixRunner` 补的冲洗/气缸模板不同口径：那是历史数据修复，这里是给人改的起点。
 * 语义与实测点一并预填——起点就不靠名称猜，改成别的工艺时只需换声明，不用改名。
 */
export function defaultTemplateParams(type: StepType): PhaseParameterDto[] {
  if (type === "Heat")
    return [
      { slotIndex: 0, name: "目标温度", engineeringUnit: "℃", setpoint: 530, min: 520, max: 540, writeToPlc: true, archiveAsQuality: true, scaleWithBatch: false, semantic: "Unspecified", measuredTag: "Temperature" },
      { slotIndex: 1, name: "升温斜率", engineeringUnit: "℃/min", setpoint: 8, min: 4, max: 12, writeToPlc: true, archiveAsQuality: false, scaleWithBatch: false, semantic: "Rate", measuredTag: null },
      { slotIndex: 2, name: "升温时长", engineeringUnit: "s", setpoint: 8, min: 0.5, max: 3600, writeToPlc: false, archiveAsQuality: false, scaleWithBatch: false, semantic: "Duration", measuredTag: null }
    ];
  if (type === "Hold")
    return [
      { slotIndex: 0, name: "保温温度", engineeringUnit: "℃", setpoint: 530, min: 525, max: 535, writeToPlc: true, archiveAsQuality: true, scaleWithBatch: false, semantic: "Unspecified", measuredTag: "Temperature" },
      { slotIndex: 1, name: "保温时长", engineeringUnit: "s", setpoint: 8, min: 1, max: 3600, writeToPlc: true, archiveAsQuality: true, scaleWithBatch: false, semantic: "Duration", measuredTag: "HoldTime" }
    ];
  if (type === "Cool")
    return [
      { slotIndex: 0, name: "终点温度", engineeringUnit: "℃", setpoint: 40, min: 20, max: 60, writeToPlc: true, archiveAsQuality: true, scaleWithBatch: false, semantic: "Unspecified", measuredTag: "Temperature" },
      { slotIndex: 1, name: "冷却时长", engineeringUnit: "s", setpoint: 6, min: 1, max: 600, writeToPlc: true, archiveAsQuality: false, scaleWithBatch: false, semantic: "Duration", measuredTag: null }
    ];
  if (type === "Mix")
    return [
      { slotIndex: 0, name: "搅拌转速", engineeringUnit: "rpm", setpoint: 60, min: 10, max: 200, writeToPlc: true, archiveAsQuality: false, scaleWithBatch: false, semantic: "Unspecified", measuredTag: null },
      { slotIndex: 1, name: "搅拌时长", engineeringUnit: "s", setpoint: 8, min: 1, max: 600, writeToPlc: true, archiveAsQuality: true, scaleWithBatch: false, semantic: "Duration", measuredTag: "HoldTime" }
    ];
  if (type === "Pressure")
    return [
      { slotIndex: 0, name: "目标压力", engineeringUnit: "bar", setpoint: 2.5, min: 1, max: 6, writeToPlc: true, archiveAsQuality: true, scaleWithBatch: false, semantic: "Unspecified", measuredTag: "Pressure" },
      { slotIndex: 1, name: "保压时长", engineeringUnit: "s", setpoint: 6, min: 1, max: 600, writeToPlc: true, archiveAsQuality: false, scaleWithBatch: false, semantic: "Duration", measuredTag: null }
    ];
  return [
    { slotIndex: 0, name: "转移量", engineeringUnit: "kg", setpoint: 50, min: 1, max: 500, writeToPlc: true, archiveAsQuality: true, scaleWithBatch: true, semantic: "Unspecified", measuredTag: null },
    { slotIndex: 1, name: "转移时长", engineeringUnit: "s", setpoint: 5, min: 1, max: 300, writeToPlc: true, archiveAsQuality: false, scaleWithBatch: false, semantic: "Duration", measuredTag: null }
  ];
}

/** 写 PLC 程序号的合法区间：7 / 8 是上位机工步占用的号，不能给工艺相用。 */
export function programOutOfRange(id: number | undefined | null): boolean {
  return id === 7 || id === 8 || (id ?? 0) < 1 || (id ?? 0) > 99;
}
