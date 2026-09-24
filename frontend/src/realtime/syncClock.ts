import { ref } from "vue";

/**
 * 最近一次**成功的 GET** 的时刻（epoch 毫秒），由 `api/http.ts` 的响应拦截打点。
 *
 * 为什么是模块级 ref 而不是 pinia store：`http.ts` 里不能引 store——那里已经写明"不直接 import router"
 * （http ← stores/auth ← router 已成环），而这个值正是拦截器要写的，走 store 就等于把同一个环再引一遍。
 *
 * 为什么要这个值：顶栏的实时徽标以前只反映**推送连接**状态。后端挂了、4 秒轮询一直 500 时，
 * 徽标照样是绿色「实时」，而屏幕上的数字其实停在最后一帧——对监控系统这是最危险的失效模式之一。
 */
export const lastSyncAt = ref(0);
