import { defineStore } from "pinia";
import { ref } from "vue";
import http from "../api/http";
import type { ProcessAlarmPageDto } from "../api/types";
import { useAuthStore } from "./auth";

/**
 * 未确认过程报警计数（侧栏「过程报警」徽标）。
 *
 * 为什么不用 Hub 推：dashboard 组会把全站执行事件灌进共享连接（监控页为此专门不订 dashboard 组），
 * 而这里只要一个数——一条 take=1 的轻查询就够。shell 每 30 秒刷新一次，报警确认成功后再手动
 * refresh，数字不会停在旧值上。取数失败保持原值：这是提示不是告警通道，
 * 数据是否新鲜由顶栏 RealtimeStatus 负责。
 */
export const useAlarmBadgeStore = defineStore("alarmBadge", () => {
  const open = ref(0);
  const auth = useAuthStore();

  async function refresh() {
    // 工艺工程师看不到「过程报警」（按角色收敛），不为他们发这条请求。
    if (!auth.can("Admin", "Operator", "Supervisor", "Quality")) return;
    try {
      const { data } = await http.get<ProcessAlarmPageDto>("/alarms", {
        params: { take: 1, onlyOpen: "true" }
      });
      open.value = data.total;
    } catch {
      /* 静默：理由见文件头 */
    }
  }

  return { open, refresh };
});