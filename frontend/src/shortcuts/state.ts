import { ref } from "vue";

/** 顶栏按钮与 ? 共用这一份开关，避免再维护第二处弹窗。 */
export const shortcutHelpOpen = ref(false);
