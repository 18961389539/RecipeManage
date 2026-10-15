/**
 * 表单对话框「误关即丢」的统一防护。
 *
 * 背景：全站含输入的对话框原先都没有关闭保护——用户按 Esc（关下拉/提示的惯性动作）
 * 或手滑点到遮罩，填了一半的设备表单（24 个字段）瞬间清空，既无确认也无恢复。
 * 审批链页对「切链」早就做了确认，同一份风险却在表单上放任，口径不一致。
 *
 * 用法：在对话框打开的同一处调用 `markClean()` 记下基线；把 `guardClose` 挂到
 * `<el-dialog :before-close="guardClose">`。表单无改动时直接放行（空表不打扰），
 * 有改动时弹一次确认，取消则对话框保持打开、内容原样还在。
 *
 * 为什么用 before-close 而不是拦截 Esc 的按键：EP 的关闭路径有三条（Esc、遮罩、
 * 右上角 ×），`before-close` 是唯一同时覆盖三条的钩子，且自带 done 回调语义。
 */
import { ElMessageBox } from "element-plus";
import { t } from "../i18n";

/** 对话框关闭请求的完成回调（EP `before-close` 的签名）。 */
type CloseDone = () => void;

export function useDialogGuard(snapshot: () => unknown) {
  let baseline: string | null = null;

  /** 在「把数据铺进表单」之后调用：记下这一刻作为「干净」基准。 */
  function markClean(): void {
    baseline = serialize(snapshot());
  }

  function isDirty(): boolean {
    return baseline !== null && serialize(snapshot()) !== baseline;
  }

  /**
   * 有未保存改动时问一句；用户确认丢弃才关闭。无改动或未 markClean 直接放行
   * （未 markClean 说明调用方没接管基线，与其误拦不如放行）。
   */
  async function guardClose(done?: CloseDone): Promise<void> {
    if (!isDirty()) {
      done?.();
      return;
    }
    try {
      await ElMessageBox.confirm(
        t("表单里有未保存的修改，关闭将丢失这些修改。"),
        t("放弃未保存的修改"),
        { type: "warning", confirmButtonText: t("关闭"), cancelButtonText: t("取消") }
      );
      done?.();
    } catch {
      /* 取消：不调用 done，对话框保持打开，用户接着改。 */
    }
  }

  return { markClean, isDirty, guardClose };
}

/**
 * 结构稳定地序列化：键按字典序排，避免「同一份数据、键序不同」被误判为改动。
 * 直接 JSON.stringify 会因为表单对象由 Object.assign 铺入而键序不一致。
 */
function serialize(value: unknown): string {
  return JSON.stringify(value, (_k, v) => {
    if (v && typeof v === "object" && !Array.isArray(v)) {
      return Object.fromEntries(Object.entries(v as Record<string, unknown>).sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0)));
    }
    return v;
  });
}
