import { expect } from "@playwright/test";
import { test } from "./helpers";
import {
  abortActiveBatches,
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

test("OPC UA Mix Pressure Transfer handshake and QualityCheck does not write PLC", async ({ page }) => {
  const health = await page.request.get("/health");
  expect(health.ok()).toBeTruthy();
  expect((await health.json()).controlRecipe).toBe("TEXT");
  await abortActiveBatches(page.request);

  const batchNo = `BOPS${uniqueStamp()}`;
  await loginAs(page, "车间操作员");
  await createBatchFromApproved(page, batchNo, "AL-PR-OPS", "UA-01", {
    "UP-混合": "UA-01",
    "UP-加压": "UA-01",
    "UP-转移": "UA-01",
    "UP-QC": "UA-01"
  });

  await page.getByRole("button", { name: "启动执行" }).click();
  await esignAndWait(page, "/start", "POST", "启动批次", passwords["车间操作员"]);
  await expect(page.locator(".page-title")).toContainText("· 待放行 ·", { timeout: 90_000 });

  const rows = await handshakeRows(page);
  expect(rows.some((r) => r.stepCode === "S10" && r.kind === "write" && (r.detail ?? "").includes("Step_Type=4"))).toBeTruthy();
  expect(rows.some((r) => r.stepCode === "S20" && r.kind === "write" && (r.detail ?? "").includes("Step_Type=5"))).toBeTruthy();
  expect(rows.some((r) => r.stepCode === "S30" && r.kind === "write" && (r.detail ?? "").includes("Step_Type=6"))).toBeTruthy();
  expect(rows.some((r) => r.stepCode === "S40" && r.kind === "write")).toBeFalsy();
  expect(rows.some((r) => r.stepCode === "S40" && r.kind === "quality" && (r.detail ?? "").includes("禁止写 PLC"))).toBeTruthy();
  expect(rows.some((r) => (r.detail ?? "").includes("UA-01") && r.kind === "trigger")).toBeTruthy();
});

test("hold during StepRunning writes Host_Hold and waits PLC_Held", async ({ page }) => {
  await abortActiveBatches(page.request);
  const batchNo = `BHLD${uniqueStamp()}`;
  await loginAs(page, "车间操作员");
  await createBatchFromApproved(page, batchNo, "AL-HT-T6", "HT-01");

  await page.getByRole("button", { name: "启动执行" }).click();
  await esignAndWait(page, "/start", "POST", "启动批次", passwords["车间操作员"]);
  await expect(page.locator(".page-title")).toContainText("StepRunning", { timeout: 30_000 });

  await page.getByRole("button", { name: "保持" }).click();
  await esignReasonAndWait(page, "/hold", "POST", "保持批次（写 Host_Hold，等待 PLC_Held，禁止盲写）", "StepRunning 保持", passwords["车间操作员"]);
  await expect(page.locator(".page-title")).toContainText("· Held ·", { timeout: 30_000 });

  const rows = await handshakeRows(page);
  expect(rows.some((r) => r.kind === "hold" && (r.detail ?? "").includes("Host_Hold"))).toBeTruthy();

  await page.getByRole("button", { name: "中止" }).click();
  await esignReasonAndWait(page, "/abort", "POST", "中止批次", "E2E 保持验证后中止", passwords["车间操作员"]);
  await expect(page.locator(".page-title")).toContainText("· Aborted ·", { timeout: 30_000 });
});
