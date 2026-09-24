import http from "./http";
import type {
  BatchDetailDto, HandshakeLogDto, ProcessAlarmDto, ProcessAlarmPageDto, SampleSeriesDto, SnapshotDriftDto
} from "./types";

/**
 * 批次监控页的接口收口（返回解包后的 data，失败一律 reject 交给调用方呈现）。
 *
 * 电子签名类动作统一走 `esignAction`：后端的 `EsignActionRequest` 就是
 * `{password, reason?, stepId?}` 一个形状，拆成六个函数只会让"忘传 stepId"这类错误没有编译期抓手。
 */

export async function getBatchDetail(id: string): Promise<BatchDetailDto> {
  return (await http.get<BatchDetailDto>(`/batches/${id}`)).data;
}

/** 趋势取数：默认上限由后端定，返回体带原始行数与抽稀步长，界面据此声明"每 N 点取 1"。 */
export async function getBatchSamples(id: string, maxPoints?: number): Promise<SampleSeriesDto> {
  return (await http.get<SampleSeriesDto>(`/batches/${id}/samples`, {
    params: maxPoints ? { maxPoints } : undefined
  })).data;
}

export async function getHandshakeLog(id: string): Promise<HandshakeLogDto[]> {
  return (await http.get<HandshakeLogDto[]>(`/batches/${id}/handshake-log`)).data;
}

export async function getBatchAlarms(id: string): Promise<{ items: ProcessAlarmDto[]; total: number }> {
  const { data } = await http.get<ProcessAlarmPageDto>(`/batches/${id}/alarms`, { params: { take: 200 } });
  return { items: data.items, total: data.total };
}

export async function getSnapshotDrift(id: string): Promise<SnapshotDriftDto[]> {
  return (await http.get<SnapshotDriftDto[]>(`/batches/${id}/snapshot-drift`)).data;
}

export interface EsignAction {
  password: string;
  reason?: string;
  /** 后端是 `Guid?`，null 与不传等价：跳步/确认没定位到具体工步时就发 null。 */
  stepId?: string | null;
}

/** `action` 取值：start / abort / hold / resume / skip / confirm。 */
export async function esignBatchAction(id: string, action: string, body: EsignAction): Promise<void> {
  await http.post(`/batches/${id}/${action}`, body);
}

export async function acknowledgeAlarm(id: string): Promise<void> {
  await http.post(`/alarms/${id}/ack`);
}
