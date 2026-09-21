import { readFile } from "node:fs/promises";
import { expect, test } from "@playwright/test";
import { fillPrompt, labeledInput, loginAs, passwords, esignAndWait } from "./helpers";

test.describe.configure({ mode: "serial" });

test("web design approve snapshot four-step handshake", async ({ page }) => {
  const health = await page.request.get("/health");
  expect(health.ok(), "API /health via Vite proxy").toBeTruthy();
  const healthBody = await health.json();
  expect(["sqlite", "postgresql"]).toContain(healthBody.database);
  expect(["text", "jsonb"]).toContain(healthBody.controlRecipe);
  if (healthBody.database === "postgresql")
    expect(healthBody.controlRecipe).toBe("jsonb");

  const stamp = new Date().toISOString().replace(/[-:T.Z]/g, "").slice(0, 12);
  const code = `E2E${stamp}`;
  const batchNo = `BE2E${stamp}`;

  await loginAs(page, "工艺工程师");
  await expect(page.getByText(/控制配方 (text|jsonb)/)).toBeVisible();
  await page.goto("/recipes");
  await page.getByRole("button", { name: "新建配方" }).click();
  const createDlg = page.getByRole("dialog", { name: "新建主配方" });
  await expect(createDlg).toBeVisible();
  await labeledInput(createDlg, "编码").fill(code);
  await labeledInput(createDlg, "名称").fill("E2E闭环热处理");
  await labeledInput(createDlg, "产品编码").fill("AL6061");
  await labeledInput(createDlg, "产品名称").fill("锻件");
  await createDlg.getByRole("button", { name: "创建" }).click();
  await page.waitForURL("**/recipes/**");
  await expect(page.getByRole("heading", { name: new RegExp(code) })).toBeVisible();

  await page.getByRole("button", { name: "+ Heat" }).click();
  const durationRow = page.locator(".el-table__body tr").filter({ hasText: "升温时长" });
  await durationRow.locator(".el-input-number input").first().fill("1");
  await page.getByRole("button", { name: "+ QualityCheck" }).click();
  await expect(page.locator(".step-item").filter({ hasText: "S10" })).toBeVisible();
  await expect(page.locator(".step-item").filter({ hasText: "S20" })).toBeVisible();

  await page.getByRole("button", { name: "提交审核" }).click();
  await fillPrompt(page, "保存工艺 · 变更控制", "E2E Procedure / Setpoints");
  await esignAndWait(page, "/procedure", "PUT", "保存工艺 · 电子签名", passwords["工艺工程师"]);
  await esignAndWait(page, "/submit", "POST", "提交审核 · 电子签名", passwords["工艺工程师"]);
  await expect(page.getByRole("button", { name: /v1 审核中/ })).toBeVisible();

  await loginAs(page, "工艺主管");
  await page.goto("/approvals");
  await page.getByRole("cell", { name: code, exact: true }).click();
  await expect(page.getByRole("heading", { name: "Parameters / Setpoints 矩阵（只读审阅）" })).toBeVisible();
  await page.getByRole("button", { name: "通过并电子签名" }).click();
  await fillPrompt(page, "当前节点：工艺主管", "路径可执行");
  await esignAndWait(page, "/decide", "POST", "电子签名", passwords["工艺主管"]);
  await expect(page.getByRole("row").filter({ hasText: code }).getByRole("cell", { name: "质量" })).toBeVisible();

  await loginAs(page, "质量工程师");
  await page.goto("/approvals");
  await page.getByRole("cell", { name: code, exact: true }).click();
  await page.getByRole("button", { name: "通过并电子签名" }).click();
  await fillPrompt(page, "当前节点：质量", "窗口合格");
  await esignAndWait(page, "/decide", "POST", "电子签名", passwords["质量工程师"]);

  await loginAs(page, "车间操作员");
  await page.goto("/batches");
  await page.getByRole("button", { name: "从已批准配方创建" }).click();
  const dialog = page.getByRole("dialog", { name: "创建批次 / 生成控制配方快照" });
  await expect(dialog).toBeVisible();
  await labeledInput(dialog, "批次号").fill(batchNo);
  await dialog.locator(".el-select").nth(0).click();
  await page.getByRole("option", { name: new RegExp(code) }).click();
  await dialog.locator(".el-select").nth(1).click();
  const idleHt01 = page.getByRole("option", { name: /^HT-01 .*空闲/ });
  const idleSim = page.getByRole("option", { name: /Simulator · 空闲/ });
  if (await idleHt01.count())
    await idleHt01.click();
  else
    await idleSim.filter({ hasNotText: "HT-02" }).first().click();
  await dialog.getByRole("button", { name: "生成快照并创建" }).click();
  await page.waitForURL("**/batches/**");
  await expect(page.getByText("快照 完整性有效")).toBeVisible();

  await page.getByRole("button", { name: "启动执行" }).click();
  await esignAndWait(page, "/start", "POST", "启动批次", passwords["车间操作员"]);

  await expect(page.locator(".page-title")).toContainText("· Completed ·", { timeout: 90_000 });
  await expect(page.getByText("快照 完整性有效")).toBeVisible();
  await expect(page.locator(".handshake-step").first()).toBeVisible();
  await expect(page.getByText("工步归档质检")).toBeVisible();
  await expect(page.getByRole("cell", { name: "硬度" }).first()).toBeVisible();

  await page.getByRole("button", { name: "电子批记录" }).click();
  await expect(page.getByRole("heading", { name: new RegExp(`电子批记录 · ${batchNo}`) })).toBeVisible();
  const downloadPromise = page.waitForEvent("download");
  await page.getByRole("button", { name: "导出 PDF/A" }).click();
  const download = await downloadPromise;
  expect(download.suggestedFilename()).toContain(`${batchNo}-eBR.pdf`);
  const pdf = await readFile(await download.path());
  const latin = pdf.toString("latin1");
  expect(latin.startsWith("%PDF")).toBeTruthy();
  expect(latin.toLowerCase()).toContain("pdfaid");

  const recordUrl = page.url();
  await loginAs(page, "质量工程师");
  await page.goto(recordUrl);
  await expect(page.getByRole("heading", { name: new RegExp(`电子批记录 · ${batchNo}`) })).toBeVisible();
  await page.getByRole("button", { name: "质量放行" }).click();
  await fillPrompt(page, "质量放行 · 放行意见", "四步握手与归档质检合格，准予放行");
  await esignAndWait(page, "/release", "POST", "质量放行 · 电子签名", passwords["质量工程师"]);
  await expect(page.locator(".page-title")).toContainText("· Released ·");
  await expect(page.getByText("四步握手与归档质检合格，准予放行")).toBeVisible();
});
