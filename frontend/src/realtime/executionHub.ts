import { onMounted, onUnmounted } from "vue";
import { HubConnectionBuilder, HubConnectionState, type HubConnection } from "@microsoft/signalr";
import { useAuthStore } from "../stores/auth";
import { useRealtimeStore } from "../stores/realtime";
import type { EquipmentOccupancyDto, EquipmentDto, ExecutionEvent } from "../api/types";

export function occupancyFromEvent(evt: ExecutionEvent | undefined | null): EquipmentOccupancyDto[] | null {
  if (!evt || evt.type !== "occupancy" || !Array.isArray(evt.payload))
    return null;
  return evt.payload as EquipmentOccupancyDto[];
}

export function applyOccupancyToEquipment(items: EquipmentDto[], rows: EquipmentOccupancyDto[]): EquipmentDto[] {
  const byId = new Map(rows.map((r) => [r.equipmentId, r]));
  return items.map((e) => {
    const occ = byId.get(e.id);
    if (!occ) return { ...e, occupancy: "Idle", occupyingBatchNo: null, occupyingBatchId: null };
    return {
      ...e,
      occupancy: occ.occupancy,
      occupyingBatchNo: occ.batchNo ?? null,
      occupyingBatchId: occ.batchId ?? null
    };
  });
}

/**
 * 全站共享一条 SignalR 连接（模块级单例 + 页面注册表）。
 *
 * 为什么：以前每个挂载页面各建一条连接——路由切换瞬间会新旧并存、每条都重交 token，
 * 服务端也按连接重复推同一批事件。现在事件在单条连接上扇出给注册中的页面，
 * 页面卸载只注销自己那份订阅（批次组显式退订，避免共享连接上残留推送）。
 */
interface PageBinding {
  onExecution?: (evt: ExecutionEvent) => void;
  subscribeDashboard: boolean;
  batchProvider?: () => string | null | undefined;
  onReconnected?: () => void | Promise<void>;
  lastBatchId?: string | null;
}

const pages = new Set<PageBinding>();
let conn: HubConnection | null = null;
let connecting: Promise<void> | null = null;

function ensureConnection(): HubConnection {
  if (conn) return conn;
  const auth = useAuthStore();
  const rt = useRealtimeStore();
  const c = new HubConnectionBuilder()
    .withUrl("/hubs/execution", {
      // 工厂每次连接/重连都重新取 token：否则重连会带着构建时的旧 token 反复 401。
      accessTokenFactory: () => auth.token
    })
    .withAutomaticReconnect()
    .build();
  c.on("execution", (evt: ExecutionEvent) => {
    rt.markEvent();
    for (const page of [...pages]) page.onExecution?.(evt);
  });
  // 自动重连期间必须让用户看得见：否则页面靠轮询"看起来仍在刷新"，
  // 实际上执行事件推送已经断了。
  c.onreconnecting(() => rt.reconnecting());
  c.onreconnected(async () => {
    rt.reconnected();
    try {
      // 重连成功 ≠ 恢复原状：新 ConnectionId 下服务端组订阅已清空，必须整表重发。
      await resubscribeAll();
    } catch {
      // 订阅恢复失败时不能显示"在线"，否则又回到"看似实时实则断流"的失效模式。
      rt.reconnecting();
    }
  });
  c.onclose(() => {
    conn = null;
    connecting = null;
    if (pages.size > 0) rt.failed();
  });
  conn = c;
  return c;
}

async function subscribePage(c: HubConnection, page: PageBinding) {
  if (page.subscribeDashboard) await c.invoke("SubscribeDashboard");
  const batchId = page.batchProvider?.();
  if (batchId) {
    page.lastBatchId = batchId;
    await c.invoke("SubscribeBatch", batchId);
  }
}

async function resubscribeAll() {
  const c = conn;
  if (!c) return;
  for (const page of [...pages]) {
    await subscribePage(c, page);
    await page.onReconnected?.();
  }
}

async function ensureStarted(): Promise<void> {
  const c = ensureConnection();
  if (c.state === HubConnectionState.Disconnected) {
    connecting ??= (async () => {
      try {
        await c.start();
        await resubscribeAll();
      } catch (e) {
        // 失败后清空，下一批挂载的页面才能重新发起，而不是永久 await 一个已拒绝的 promise。
        connecting = null;
        throw e;
      }
      connecting = null;
    })();
    await connecting;
    return;
  }
  if (c.state === HubConnectionState.Connecting)
    await connecting;
}

export function useExecutionHub(options: {
  onExecution?: (evt: ExecutionEvent) => void;
  subscribeDashboard?: boolean;
  /** 页面级重订阅钩子：服务端按 ConnectionId 分组，重连后旧订阅全部失效。 */
  onReconnected?: () => void | Promise<void>;
  /**
   * 本页要单独订阅的批次 id。服务端按 `batch:{id}` 分组推送，不订阅就一条事件都收不到。
   * 用取值函数而非字符串：批次 id 通常要等详情接口回来才有，重连时也要重新问一次。
   */
  subscribeBatch?: () => string | null | undefined;
}) {
  const rt = useRealtimeStore();
  const page: PageBinding = {
    onExecution: options.onExecution,
    subscribeDashboard: options.subscribeDashboard !== false,
    batchProvider: options.subscribeBatch,
    onReconnected: options.onReconnected
  };

  onMounted(async () => {
    pages.add(page);
    try {
      await ensureStarted();
      rt.attached();
    } catch {
      // start() 失败不能无感知：状态灯直接落"已断开"。
      rt.failed();
    }
  });

  onUnmounted(() => {
    pages.delete(page);
    rt.detached();
    // 共享连接上必须显式退订批次组，否则离开详情页后该批次仍持续推给这条连接。
    const c = conn;
    if (c && page.lastBatchId && c.state === HubConnectionState.Connected)
      void c.invoke("UnsubscribeBatch", page.lastBatchId).catch(() => undefined);
  });

  return {
    /**
     * 补发一次本页订阅。详情页必须先拿到批次 id 再调用——连接起来的时机比接口早，
     * 只靠 onMounted 里那次订阅会订到 null。
     */
    resubscribe: async () => {
      const c = conn;
      if (!c || c.state !== HubConnectionState.Connected) return;
      await subscribePage(c, page);
      await options.onReconnected?.();
    }
  };
}

/** 退出登录时拆掉整条共享连接；下次挂载页面会重建。 */
export function stopExecutionHub() {
  const c = conn;
  conn = null;
  connecting = null;
  pages.clear();
  if (c) void c.stop().catch(() => undefined);
}
