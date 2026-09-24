import { describe, expect, it } from "vitest";
import {
  DEFAULT_WATCHDOG, PARAM_SLOTS, defaultMeasured, emptyTagMap, parseTagMap, parseWatchdog, serializeTagMap, serializeWatchdog
} from "./tagMap";

/**
 * 握手点表翻译是"界面填的地址 → PLC 实际写入偏移"的唯一一道转换，
 * 出错的表现是写参落到别的地址上（现场误动作），所以这里钉的是键名与槽位，不是渲染。
 */
describe("tagMap 点表映射", () => {
  it("默认布局：握手位集中在 DB10.0–16，写参从 DB10.20 起每 4 字节一槽", () => {
    const map = emptyTagMap();
    expect(map.params).toHaveLength(PARAM_SLOTS);
    expect(map.params[0]).toBe("DB10.20");
    expect(map.params[15]).toBe("DB10.80");
    expect(map.plcReady).toBe("DB10.8.1");
  });

  it("PascalCase 与 camelCase 两种历史写法都读得出来", () => {
    const pascal = parseTagMap('{"PlcReady":"DB20.1","HostHold":"DB20.2","Measured":{"Temperature":"DB30.0"}}');
    expect(pascal.plcReady).toBe("DB20.1");
    expect(pascal.hostHold).toBe("DB20.2");
    expect(pascal.measured).toEqual([{ name: "Temperature", address: "DB30.0" }]);

    const camel = parseTagMap('{"plcReady":"DB21.1","hostHold":"DB21.2","measured":{"Pressure":"DB31.0"}}');
    expect(camel.plcReady).toBe("DB21.1");
    expect(camel.hostHold).toBe("DB21.2");
    expect(camel.measured).toEqual([{ name: "Pressure", address: "DB31.0" }]);
  });

  it("实测点不限于 Temperature/Pressure/HoldTime，其它工艺量原样进出", () => {
    // 表单以前把 measured 写死成三个键，编辑一次设备就会把 Viscosity 这类点静默删掉。
    const map = parseTagMap('{"Measured":{"Viscosity":"DB40.0","FlowRate":"DB40.4","Temperature":"DB40.8"}}');
    expect(map.measured.map((m) => m.name)).toEqual(["Viscosity", "FlowRate", "Temperature"]);
    expect(parseTagMap(serializeTagMap(map)).measured).toEqual(map.measured);
  });

  it("点表写明没有实测点时不拿默认 DB10.x 去补", () => {
    expect(parseTagMap('{"Measured":{}}').measured).toEqual([]);
    expect(parseTagMap("{}").measured).toEqual(defaultMeasured());
    expect(emptyTagMap().measured).toEqual(defaultMeasured());
  });

  it("存出去一律 PascalCase（后端 HandshakeTagMap 的属性名口径）", () => {
    const json = serializeTagMap({ ...emptyTagMap(), plcReady: "DB9.1" });
    const raw = JSON.parse(json) as Record<string, unknown>;
    expect(raw.PlcReady).toBe("DB9.1");
    expect(raw.plcReady).toBeUndefined();
    expect(raw.Params).toBeTypeOf("object");
  });

  it("序列化再读回必须等价，否则改一个地址会在下一次打开时丢掉", () => {
    const original = {
      ...emptyTagMap(),
      stepId: "DB12.0",
      hostHold: "DB12.9",
      params: Array.from({ length: PARAM_SLOTS }, (_, i) => `DB12.${100 + i}`),
      measured: [
        { name: "Temperature", address: "DB12.200" },
        { name: "Pressure", address: "DB12.204" },
        { name: "HoldTime", address: "DB12.208" }
      ],
      opcUaUseSecurity: true,
      opcUaUser: "brmes"
    };
    expect(parseTagMap(serializeTagMap(original))).toEqual(original);
  });

  it("点表里写参槽不足 16 个时按位补默认，不能留下 undefined", () => {
    const map = parseTagMap('{"Params":["DB7.20","DB7.24"]}');
    expect(map.params).toHaveLength(PARAM_SLOTS);
    expect(map.params[0]).toBe("DB7.20");
    expect(map.params[1]).toBe("DB7.24");
    expect(map.params[2]).toBe("DB10.28");
    expect(map.params.some((p) => !p)).toBe(false);
  });

  it("坏 JSON 必须抛，不能静默回退默认点表", () => {
    // 回退默认值=把 DB10.x 写回一台点表本来完全不同的真机，这个失败模式比打不开对话框严重。
    expect(() => parseTagMap("{ 这不是 JSON")).toThrow();
    expect(parseTagMap(null)).toEqual(emptyTagMap());
    expect(parseTagMap("")).toEqual(emptyTagMap());
  });

  it("看门狗只覆盖写了的项，坏 JSON 回退默认（超时项回退是保守，不是写错地址）", () => {
    expect(parseWatchdog('{"ackTimeoutSeconds":30}')).toEqual({ ...DEFAULT_WATCHDOG, ackTimeoutSeconds: 30 });
    expect(parseWatchdog(undefined)).toEqual(DEFAULT_WATCHDOG);
    expect(parseWatchdog("nonsense")).toEqual(DEFAULT_WATCHDOG);
    expect(JSON.parse(serializeWatchdog(parseWatchdog("{}")))).toEqual(DEFAULT_WATCHDOG);
  });
});
