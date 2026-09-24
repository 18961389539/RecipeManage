import http from "./http";
import type {
  ConnectionTestDto,
  EquipmentClassDto,
  EquipmentDto,
  TagMapCheckDto,
  UpsertEquipmentRequest,
  UpsertPhaseTemplateRequest
} from "./types";

/**
 * 设备与相库的接口收口：URL 拼装与解包只写一次，调用方拿到的是 data 而不是 AxiosResponse。
 *
 * 不改错误语义：失败一律 reject，由调用方决定怎么呈现——列表取数用页内 el-alert（4 秒轮询下
 * toast 会刷屏），单条操作用 toast。这一层不做统一弹窗，否则两种口径就没法区分了。
 */

export async function listEquipment(): Promise<EquipmentDto[]> {
  return (await http.get<EquipmentDto[]>("/equipment")).data;
}

export async function listEquipmentClasses(): Promise<EquipmentClassDto[]> {
  return (await http.get<EquipmentClassDto[]>("/equipment/classes")).data;
}

/** `id` 为空即新建；请求体里带着 id 也无妨，后端按路由参数定位。 */
export async function saveEquipment(
  id: string | null,
  body: UpsertEquipmentRequest & { id?: string }
): Promise<EquipmentDto> {
  return id
    ? (await http.put<EquipmentDto>(`/equipment/${id}`, body)).data
    : (await http.post<EquipmentDto>("/equipment", body)).data;
}

export async function validateTagMap(id: string): Promise<TagMapCheckDto> {
  return (await http.post<TagMapCheckDto>(`/equipment/${id}/validate-tagmap`)).data;
}

export async function testConnection(id: string): Promise<ConnectionTestDto> {
  return (await http.post<ConnectionTestDto>(`/equipment/${id}/test-connection`)).data;
}

/** 只作用于 Simulator 设备；后端会先落审计再改仿真状态。 */
export async function injectSimulatorFault(id: string, mode: string): Promise<TagMapCheckDto> {
  return (await http.post<TagMapCheckDto>(`/equipment/${id}/inject-fault`, { mode })).data;
}

export async function savePhaseTemplate(
  classId: string,
  templateId: string | null,
  body: UpsertPhaseTemplateRequest
): Promise<void> {
  if (templateId) await http.put(`/equipment/classes/${classId}/templates/${templateId}`, body);
  else await http.post(`/equipment/classes/${classId}/templates`, body);
}

export async function deletePhaseTemplate(classId: string, templateId: string): Promise<void> {
  await http.delete(`/equipment/classes/${classId}/templates/${templateId}`);
}
