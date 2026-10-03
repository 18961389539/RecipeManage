import { expect } from "@playwright/test";
import { test } from "./helpers";
import {
  createBatchFromApproved,
  esignAndWait,
  fillPrompt,
  handshakeRows,
  loginAs,
  passwords,
  uniqueStamp,
  esignReasonAndWait,
} from "./helpers";

test.describe.configure({ mode: "serial" });

test("manual confirm does not write PLC and shows Temperature trends", async ({ page }) => {
  const health = await page.request.get("/health");
  expect(health.ok()).toBeTruthy();
  expect((await health.json()).controlRecipe).toBe("TEXT");

  const stamp = uniqueStamp();
  await loginAs(page, "车间操作员");
  await createBatchFromApproved(page, `BCFM${stamp}`, "AL-HT-CFM", "HT-01");

  await expect(page.getByText("PLC 写参计划")).toBeVisible();
  const plan = page.locator(".write-plan");
  await expect(plan).toContainText("S10");
  await expect(plan).toContainText("S20");
  await expect(plan).toContainText("禁止写 PLC");
  await expect(plan).toContainText("Param[0]=120");

  await page.getByRole("button", { name: "启动执行" }).click();
  await esignAndWait(page, "/start", "POST", "启动批次", passwords["车间操作员"]);

  await expect(page.getByRole("button", { name: "人工确认" })).toBeVisible({ timeout: 45_000 });
  await expect(page.locator(".handshake-status").filter({ hasText: "等待人工确认" }).first()).toBeVisible();
  await expect(page.locator(".u-legend")).toContainText("Temperature");
  const token = await page.evaluate(() => localStorage.getItem("rm_token"));
  const id = page.url().split("/batches/")[1]?.split(/[?#]/)[0];
  const samplesResp = await page.request.get(`/api/batches/${id}/samples`, {
    headers: { Authorization: `Bearer ${token}` }
  });
  expect(samplesResp.ok()).toBeTruthy();
  const samples = (await samplesResp.json()) as { tag: string }[];
  expect(samples.some((s) => s.tag === "Temperature")).toBeTruthy();

  await page.getByText("ECharts", { exact: true }).click();
  await expect(page.locator("[_echarts_instance_] canvas, .echarts canvas, canvas").first()).toBeVisible();
  await page.getByText("uPlot", { exact: true }).click();

  await page.getByRole("button", { name: "人工确认" }).click();
  await esignReasonAndWait(page, "/confirm", "POST", "人工确认本工步（禁止写 PLC）", "现场确认放行", passwords["车间操作员"]);
  await expect(page.locator(".page-title")).toContainText("· 待放行 ·", { timeout: 30_000 });

  const rows = await handshakeRows(page);
  expect(rows.some((r) => r.stepCode === "S10" && r.kind === "write")).toBeTruthy();
  expect(rows.some((r) => r.stepCode === "S20" && r.kind === "write")).toBeFalsy();
  expect(rows.some((r) => r.stepCode === "S20" && r.kind === "confirm")).toBeTruthy();
});

test("quality OOS holds and does not write the next step", async ({ page }) => {
  const stamp = uniqueStamp();
  await loginAs(page, "车间操作员");
  await createBatchFromApproved(page, `BOOS${stamp}`, "AL-HT-OOS", "HT-01");

  await page.getByRole("button", { name: "启动执行" }).click();
  await esignAndWait(page, "/start", "POST", "启动批次", passwords["车间操作员"]);
  await expect(page.locator(".page-title")).toContainText("· Held ·", { timeout: 45_000 });
  await expect(page.locator(".el-alert__title").filter({ hasText: "质检超差" })).toBeVisible();

  const rows = await handshakeRows(page);
  expect(rows.some((r) => r.stepCode === "S10" && r.kind === "write")).toBeTruthy();
  expect(rows.some((r) => r.kind === "quality" && (r.detail ?? "").includes("超差"))).toBeTruthy();
  expect(rows.some((r) => r.stepCode === "S20" && (r.kind === "write" || r.kind === "trigger"))).toBeFalsy();
});
