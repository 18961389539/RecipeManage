import { ElMessageBox } from "element-plus";

/**
 * GxP 电子签名弹窗：以再次输入登录密码作为"确系本人"的电子签名，由后端
 * RequireEsignAsync 校验。全站签名弹窗的提示文案、输入类型、标题模式在此统一，
 * 各视图只传动作名，避免 9+ 处内联复制后文案分叉。
 * @param title 动作名（自动拼" · 电子签名"标题）
 * @param meaning 可选的签署含义声明（GxP 要求说明签的是什么）；缺省为标准提示
 */
export async function esignPassword(title: string, meaning?: string): Promise<string> {
  const { value: password } = await ElMessageBox.prompt(
    meaning ?? "请再次输入登录密码作为电子签名",
    `${title} · 电子签名`,
    { inputType: "password", inputPlaceholder: "再次输入登录密码" }
  );
  return password;
}