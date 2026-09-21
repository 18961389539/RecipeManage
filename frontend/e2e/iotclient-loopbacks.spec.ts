import { expect, test } from "@playwright/test";
import {
  abortActiveBatches,
  createBatchFromApproved,
  esignAndWait,
  fillPrompt,
  handshakeRows,
  loginAs,
  passwords,
  uniqueStamp
} from "./helpers";

test.describe.configure({ mode: "serial" });

test("IOTClient loopback connection tests and Modbus handshake", async ({ page }) => {
  const health = await page.request.get("/health");
  expect(health.ok()).toBeTruthy();
  expect((await health.json()).controlRecipe).toBe("jsonb");
  await abortActiveBatches(page.request);

  await loginAs(page, "车间操作员");
  await page.goto("/equipment");
  await expect(page.getByRole("heading", { name: "设备与 PLC 驱动" })).toBeVisible();

  for (const code of ["MB-01", "S7-01", "UA-01"]) {
    await page.getByRole("row", { name: new RegExp(code) }).getByRole("button", { name: "测试连接" }).click();
    await expect(page.locator(".el-message").filter({ hasText: /PLC_Ready/ })).toBeVisible();
    await page.locator(".el-message").filter({ hasText: /PLC_Ready/ }).first().waitFor({ state: "hidden", timeout: 8_000 }).catch(() => undefined);
  }

  const batchNo = `BMBW${uniqueStamp()}`;
  await createBatchFromApproved(page, batchNo, "AL-HT-WAIT", "MB-01");
  await page.getByRole("button", { name: "启动执行" }).click();
  await esignAndWait(page, "/start", "POST", "启动批次", passwords["车间操作员"]);
  await expect(page.locator(".page-title")).toContainText("· Completed ·", { timeout: 90_000 });

  const rows = await handshakeRows(page);
  expect(rows.some((r) => r.stepCode === "S10" && r.kind === "write" && (r.detail ?? "").includes("Step_Type=1"))).toBeTruthy();
  expect(rows.some((r) => (r.detail ?? "").includes("MB-01") && r.kind === "trigger")).toBeTruthy();
  expect(rows.some((r) => r.stepCode === "S20" && r.kind === "write")).toBeFalsy();
  expect(rows.some((r) => r.stepCode === "S20" && r.kind === "wait")).toBeTruthy();
});

test("IOTClient Siemens S7 four-step handshake does not write ManualConfirm", async ({ page }) => {
  await abortActiveBatches(page.request);
  const batchNo = `BS7${uniqueStamp()}`;
  await loginAs(page, "车间操作员");
  await createBatchFromApproved(page, batchNo, "AL-HT-CFM", "S7-01");

  await page.getByRole("button", { name: "启动执行" }).click();
  await esignAndWait(page, "/start", "POST", "启动批次", passwords["车间操作员"]);
  await expect(page.getByRole("button", { name: "人工确认" })).toBeVisible({ timeout: 45_000 });
  await page.getByRole("button", { name: "人工确认" }).click();
  await fillPrompt(page, "人工确认本工步（禁止写 PLC）", "S7 环回确认");
  await esignAndWait(page, "/confirm", "POST", "人工确认本工步（禁止写 PLC）", passwords["车间操作员"]);
  await expect(page.locator(".page-title")).toContainText("· Completed ·", { timeout: 30_000 });

  const rows = await handshakeRows(page);
  expect(rows.some((r) => r.stepCode === "S10" && r.kind === "write" && (r.detail ?? "").includes("Step_Type=1"))).toBeTruthy();
  expect(rows.some((r) => (r.detail ?? "").includes("S7-01") && r.kind === "trigger")).toBeTruthy();
  expect(rows.some((r) => r.stepCode === "S20" && r.kind === "write")).toBeFalsy();
  expect(rows.some((r) => r.stepCode === "S20" && r.kind === "confirm")).toBeTruthy();
});
