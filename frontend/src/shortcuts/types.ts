export interface ShortcutSpec {
  id: string;
  /** 如 ctrl+s、f8、?、/ */
  chord: string;
  /** 列表/帮助里的动作名 */
  label: string;
  /** 帮助分组，如「全局」「批次监控」 */
  group: string;
  run: () => void | Promise<void>;
  when?: () => boolean;
  /** Ctrl 组合可在输入框里触发（保存、提交）；字母键不行 */
  allowInInput?: boolean;
}
