import { expect, type Page } from "@playwright/test";
import { test } from "./helpers";
import {
  addPhaseFromTemplate,
  abortActiveBatches,
  createBatchFromApproved,
  esignAndWait,
  fillPrompt,
  handshakeRows,
  labeledInput,
  loginAs,
  passwords,
  uniqueStamp,
  esignReasonAndWait,
} from "./helpers";

test.describe.configure({ mode: "serial" });

async function approveSubmitted(page: Page, code: string, supervisorNote: string, qaNote: string) {
  await loginAs(page, "工艺主管");
  await page.goto("/approvals");
  await page.getByRole("cell", { name: code, exact: true }).click();
  await page.getByRole("button", { name: "通过并电子签名" }).click();
  await esignReasonAndWait(page, "/decide", "POST", "当前节点：工艺主管", supervisorNote, passwords["工艺主管"]);

  await loginAs(page, "质量工程师");
  await page.goto("/approvals");
  await page.getByRole("cell", { name: code, exact: true }).click();
  await page.getByRole("button", { name: "通过并电子签名" }).click();
  await esignReasonAndWait(page, "/decide", "POST", "当前节点：质量", qaNote, passwords["质量工程师"]);
}

test("reject reopen scaled control recipe snapshot writes Transfer quantity", async ({ page }) => {
  const health = await page.request.get("/health");
  expect(health.ok()).toBeTruthy();
  expect((await health.json()).controlRecipe).toBe("TEXT");
  await abortActiveBatches(page.request);

  const stamp = uniqueStamp();
  const code = `SCL${stamp}`;
  const lot = `LOT-${stamp}`;
  const batchNo = `BSCL${stamp}`;

  await loginAs(page, "工艺工程师");
  await page.goto("/recipes");
  await page.getByRole("button", { name: "新建配方" }).click();
  const createDlg = page.getByRole("dialog", { name: "新建主配方" });
  await labeledInput(createDlg, "编码").fill(code);
  await labeledInput(createDlg, "名称").fill("E2E驳回缩放转移");
  await labeledInput(createDlg, "产品编码").fill("AL6061");
  await labeledInput(createDlg, "产品名称").fill("锻件");
  await createDlg.getByRole("button", { name: "创建" }).click();
  await page.waitForURL("**/recipes/**");

  await addPhaseFromTemplate(page, "GENERIC", "PH-XFER");
  await page.locator(".el-table__body tr").filter({ hasText: "转移时长" }).locator(".el-input-number input").first().fill("1");
  await expect(page.locator(".el-table__body tr").filter({ hasText: "转移量" })).toBeVisible();
  await page.getByRole("button", { name: "+ 质检" }).click();

  await page.getByRole("button", { name: "提交审核" }).click();
  await esignReasonAndWait(page, "/procedure", "PUT", "保存工艺 · 电子签名", "v1 Transfer+QC", passwords["工艺工程师"]);
  await esignAndWait(page, "/submit", "POST", "提交审核 · 电子签名", passwords["工艺工程师"]);

  await loginAs(page, "工艺主管");
  await page.goto("/approvals");
  await page.getByRole("cell", { name: code, exact: true }).click();
  await page.getByRole("button", { name: "驳回" }).click();
  await esignReasonAndWait(page, "/decide", "POST", "当前节点：工艺主管", "路径需返工后再审", passwords["工艺主管"]);

  await loginAs(page, "工艺工程师");
  await page.goto("/recipes");
  await page.getByRole("cell", { name: code, exact: true }).click();
  await expect(page.locator(".ver.on")).toContainText("驳回");
  await page.getByRole("button", { name: "重新打开" }).click();
  await esignAndWait(page, "/reopen", "POST", "重新打开驳回版本 · 电子签名", passwords["工艺工程师"]);
  await expect(page.getByRole("button", { name: "提交审核" })).toBeVisible();
  await expect(page.locator(".ver.on")).toContainText("草稿");

  await page.getByRole("button", { name: "提交审核" }).click();
  await esignReasonAndWait(page, "/procedure", "PUT", "保存工艺 · 电子签名", "驳回后重开再提交", passwords["工艺工程师"]);
  await esignAndWait(page, "/submit", "POST", "提交审核 · 电子签名", passwords["工艺工程师"]);
  await approveSubmitted(page, code, "返工后路径可执行", "窗口合格");

  await loginAs(page, "车间操作员");
  await createBatchFromApproved(page, batchNo, code, "HT-01", undefined, { scaleFactor: 2, lotNumber: lot });
  await expect(page.locator(".page-title")).toContainText("缩放 ×2");
  await expect(page.locator(".page-title")).toContainText(`物料 ${lot}`);
  await expect(page.getByText("快照：完整性有效")).toBeVisible();

  const token = await page.evaluate(() => localStorage.getItem("rm_token"));
  const id = page.url().split("/batches/")[1]?.split(/[?#]/)[0];
  const detail = await page.request.get(`/api/batches/${id}`, {
    headers: { Authorization: `Bearer ${token}` }
  });
  expect(detail.ok()).toBeTruthy();
  const body = await detail.json() as {
    snapshotIntegrity: string;
    snapshot: {
      scaleFactor?: number | null;
      lotNumber?: string | null;
      steps: { type: string; parameters: { name: string; setpoint: number; min?: number | null; max?: number | null }[] }[];
    };
  };
  expect(body.snapshotIntegrity).toBe("Valid");
  expect(body.snapshot.scaleFactor).toBe(2);
  expect(body.snapshot.lotNumber).toBe(lot);
  const transfer = body.snapshot.steps.find((s) => s.type === "Transfer");
  const qty = transfer?.parameters.find((p) => p.name === "转移量");
  const duration = transfer?.parameters.find((p) => p.name === "转移时长");
  expect(qty?.setpoint).toBe(100);
  expect(qty?.min).toBe(2);
  expect(qty?.max).toBe(1000);
  expect(duration?.setpoint).toBe(1);

  await page.getByRole("button", { name: "启动执行" }).click();
  await esignAndWait(page, "/start", "POST", "启动批次", passwords["车间操作员"]);
  await expect(page.locator(".page-title")).toContainText("· 待放行 ·", { timeout: 90_000 });

  const rows = await handshakeRows(page);
  expect(rows.some((r) => r.stepCode === "S10" && r.kind === "write" && (r.detail ?? "").includes("Step_Type=6"))).toBeTruthy();
  expect(rows.some((r) => r.kind === "verify" && (r.detail ?? "").includes("Param[0]=100"))).toBeTruthy();
  expect(rows.some((r) => r.stepCode === "S20" && r.kind === "write")).toBeFalsy();
  expect(rows.some((r) => r.stepCode === "S20" && r.kind === "quality" && (r.detail ?? "").includes("禁止写 PLC"))).toBeTruthy();
});
