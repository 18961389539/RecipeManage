import axios from "axios";
import { lastSyncAt } from "../realtime/syncClock";
import { currentLocale } from "../i18n";

const http = axios.create({ baseURL: "/api" });

http.interceptors.request.use((config) => {
  const token = localStorage.getItem("rm_token");
  if (token) config.headers.Authorization = `Bearer ${token}`;
  // 带上界面语言，后端才知道该把校验/权限提示译成哪种语言。
  // 只影响瞬时提示：审计与电子签名原文不经过这条路径，不会因此被改写。
  config.headers["Accept-Language"] = currentLocale();
  return config;
});

/**
 * 会话过期回调，由 main.ts 注入（那里才拿得到 router 和 ElMessage）。
 * 不直接 import router：http <- stores/auth <- router 已成环，在 http 里引 router 会绕成循环依赖。
 */
let onExpired: ((from: string) => void) | null = null;
export function reportSessionExpired(fn: (from: string) => void): void {
  onExpired = fn;
}

// 各列表页 4 秒轮询，token 一过期往往多个请求同时 401；不去重就会连弹多条同样的提示。
// 任意一次成功响应即代表会话有效，届时复位。
let expiredHandled = false;

/**
 * 带 HTTP 状态码的 Error。
 *
 * 视图要按"连不上后端"和"后端返回 503"给不同文案时，不该去正则 axios 的英文原文
 * （"Request failed with status code 503" 直接贴到中文界面上就是这里修掉的）。
 */
export class HttpError extends Error {
  readonly status?: number;

  constructor(message: string, status?: number) {
    super(message);
    this.name = "HttpError";
    this.status = status;
  }
}

/** 存活探针：它 200 只代表进程还在，不代表屏幕上的数字拿到了新数据。 */
function isLivenessProbe(url?: string): boolean {
  return !!url && url.split("?")[0].endsWith("/health");
}

http.interceptors.response.use(
  (r) => {
    expiredHandled = false;
    // 只有成功的读请求才算"页面又拿到一次新数据"；写请求不算（它不刷新屏幕上的数字）。
    // /health 也不算：数据接口整片挂掉时它照样 200，把它计入新鲜度就会让顶栏
    // 在屏幕上全是最后一帧的时候仍然显示「实时」——监控系统里最危险的失效模式之一。
    if (r.config?.method === "get" && !isLivenessProbe(r.config?.url)) lastSyncAt.value = Date.now();
    return r;
  },
  (err) => {
    if (err.response?.status === 401 && !location.pathname.startsWith("/login")) {
      const from = location.pathname + location.search;
      localStorage.removeItem("rm_token");
      localStorage.removeItem("rm_user");
      if (!expiredHandled) {
        expiredHandled = true;
        onExpired?.(from);
      }
      // 回调未注册（理论上只在 main.ts 完成装配前）时退回整页跳转，至少不会卡在失效页面上。
      if (!onExpired) location.assign("/login");
    }
    const status = typeof err.response?.status === "number" ? (err.response.status as number) : undefined;
    const message = err.response?.data?.message ?? err.message ?? "请求失败";
    return Promise.reject(new HttpError(message, status));
  }
);

export default http;
