/**
 * 列表页统一的取数状态。
 *
 * 背景：原先各视图在 onMounted 里裸 await 取数——首屏空窗期用户看到的是空表/占位 0，
 * 且一旦请求失败既无提示也无任何反馈（4 秒轮询还会持续 reject）。
 * 这里把 loading / error 收拢成同一套语义：
 *   - loading 仅首屏为 true，轮询刷新不再闪mask，避免视觉抖动；
 *   - error 由视图以 el-alert 就地展示，不用 toast（轮询下 toast 会刷屏）。
 */
import { ref } from "vue";

export function useLoad() {
  const loading = ref(true);
  const error = ref("");

  /**
   * @param task axios 请求本身（注意 http.get<T>() 返回的是 Promise<AxiosResponse<T>>，
   *             这里按 { data: T } 解包，调用方无需自己 .data，避免又把整个 Response 当成数据。
   * @param apply 回调。返回类型用 unknown 而非 void —— 目标是 void 时 TS 才允许任意返回值，
   *              一旦写成 `void | Promise<void>` 联合类型，简洁写法 `(d) => (x.value = d)` 会因返回数组而报错。
   *              允许异步（如拿到列表后继续拉详情），此处 await 住以便其异常也进入同一错误处理。
   */
  async function run<T>(task: Promise<{ data: T }>, apply: (data: T) => unknown): Promise<void> {
    return settle(task, (r) => apply(r.data));
  }

  /**
   * 与 run 同一套 loading/error 语义，只是收口的服务层函数已经解过包了
   * （`api/equipment.ts` 那类返回 `Promise<T>` 而不是 `Promise<AxiosResponse<T>>`）。
   * 分成两个名字而不是靠"有没有 .data 属性"去猜：DTO 自己也可能有个叫 data 的字段。
   */
  async function runValue<T>(task: Promise<T>, apply: (data: T) => unknown): Promise<void> {
    return settle(task, apply);
  }

  async function settle<T>(task: Promise<T>, apply: (data: T) => unknown): Promise<void> {
    try {
      await apply(await task);
      error.value = "";
    } catch (e) {
      error.value = (e as Error).message || "数据加载失败";
    } finally {
      loading.value = false;
    }
  }

  return { loading, error, run, runValue };
}
