import axios from "axios";

const http = axios.create({ baseURL: "/api" });

http.interceptors.request.use((config) => {
  const token = localStorage.getItem("rm_token");
  if (token) config.headers.Authorization = `Bearer ${token}`;
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

http.interceptors.response.use(
  (r) => {
    expiredHandled = false;
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
    const message = err.response?.data?.message ?? err.message ?? "请求失败";
    return Promise.reject(new Error(message));
  }
);

export default http;
