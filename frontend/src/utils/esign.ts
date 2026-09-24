import { ElMessage, ElMessageBox } from "element-plus";
import { t } from "../i18n";
import { ElInput } from "element-plus";
import { h, reactive } from "vue";

/**
 * GxP 电子签名弹窗：以再次输入登录密码作为"确系本人"的电子签名，由后端
 * RequireEsignAsync 校验。全站签名弹窗的提示文案、输入类型、标题模式在此统一，
 * 各视图只传动作名，避免 9+ 处内联复制后文案分叉。
 * @param title 动作名（自动拼" · 电子签名"标题）
 * @param meaning 可选的签署含义声明（GxP 要求说明签的是什么）；缺省为标准提示
 */
export async function esignPassword(title: string, meaning?: string): Promise<string> {
  const { value: password } = await ElMessageBox.prompt(
    meaning ?? t("请再次输入登录密码作为电子签名。"),
    esignTitle(title),
    { inputType: "password", inputPlaceholder: t("再次输入登录密码") }
  );
  return password;
}

/**
 * 弹窗标题的唯一拼法。传进来的是**中文动作名**（也是 i18n 的键），这里统一过 t()，
 * 视图侧就不必各自记得包；已经包过的再包一次是原样返回，不会译两遍。
 */
const esignTitle = (title: string) => t("{0} · 电子签名", t(title));

export interface EsignFields {
  reason: string;
  password: string;
}

/**
 * 需要「原因 / 意见 + 密码」的签名：一屏两栏，一次打断。
 *
 * 为什么合并：原先是 prompt(原因) → prompt(密码) 两个弹窗串联，第二屏不再复述"签的是什么"，
 * 操作员要凭记忆确认刚填的原因对应哪个动作；中止、保持、跳步、人工确认、放行、驳回
 * 全都要连点两次确定。
 *
 * 合并的是打断次数，不是字段：密码仍单独一栏且 type=password，含义声明仍写在最前面。
 * 校验放在 beforeClose —— 空值时不关窗，让操作者原地补填，而不是抛错重开一遍。
 * @param reasonHint 填这一栏的现场指引（如"超差时必须说明偏差放行理由"）；
 *                   它不是签署含义，含义声明另走 meaning，两行都要看得见。
 */
export async function esignWithReason(
  title: string,
  meaning: string,
  reasonLabel: string,
  reasonRequired = true,
  reasonHint = ""
): Promise<EsignFields> {
  const fields = reactive({ reason: "", password: "" });
  try {
    await ElMessageBox({
      title: esignTitle(title),
      message: () => h("div", { class: "esign-form" }, [
        h("p", { class: "esign-meaning" }, meaning),
        ...(reasonHint ? [h("p", { class: "esign-hint" }, t(reasonHint))] : []),
        h("label", { class: "esign-label" }, t(reasonLabel)),
        h(ElInput, {
          modelValue: fields.reason,
          "onUpdate:modelValue": (v: string) => { fields.reason = v; },
          type: "textarea",
          rows: 2,
          maxlength: 400,
          placeholder: reasonRequired ? t("必填") : t("可选")
        }),
        h("label", { class: "esign-label" }, t("登录密码（电子签名）")),
        h(ElInput, {
          modelValue: fields.password,
          "onUpdate:modelValue": (v: string) => { fields.password = v; },
          type: "password",
          placeholder: t("再次输入登录密码")
        })
      ]),
      showCancelButton: true,
      confirmButtonText: t("签名并确认"),
      cancelButtonText: t("取消"),
      beforeClose: (action, _instance, done) => {
        if (action === "confirm") {
          if (reasonRequired && !fields.reason.trim()) {
            ElMessage.warning(t("请填写{0}。", t(reasonLabel)));
            return;
          }
          if (!fields.password) {
            ElMessage.warning(t("电子签名需要再次输入登录密码。"));
            return;
          }
        }
        done();
      }
    });
  } catch (e) {
    // Esc / 点遮罩关掉时 EP 抛的是 "close"，调用方统一按 "cancel" 判断；
    // 不归一化就会在取消签名后弹一条内容为 "close" 的错误提示。
    throw e === "close" ? "cancel" : e;
  }
  return { reason: fields.reason.trim(), password: fields.password };
}
