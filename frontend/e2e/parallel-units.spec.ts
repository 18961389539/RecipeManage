import { expect, test } from "@playwright/test";
import {
  abortActiveBatches,
  createBatchFromApproved,
  esignAndWait,
  fillPrompt,
  handshakeRows,
  labeledInput,
  addPhaseFromTemplate,
  loginAs,
  passwords,
  uniqueStamp,
  esignReasonAndWait,
} from "./helpers";

test.describe.configure({ mode: "serial" });

test("design parallel unit procedures and execute on two PLCs", async ({ page }) => {
  const health = await page.request.get("/health");
  expect(health.ok()).toBeTruthy();
  expect((await health.json()).controlRecipe).toBe("TEXT");
  await abortActiveBatches(page.request);

  const stamp = uniqueStamp();
  const code = `P2U${stamp}`;
  const batchNo = `BP2U${stamp}`;

  await loginAs(page, "工艺工程师");
  await page.goto("/recipes");
  await page.getByRole("button", { name: "新建配方" }).click();
  const createDlg = page.getByRole("dialog", { name: "新建主配方" });
  await labeledInput(createDlg, "编码").fill(code);
  await labeledInput(createDlg, "名称").fill("E2E双单元并行");
  await labeledInput(createDlg, "产品编码").fill("AL6061");
  await labeledInput(createDlg, "产品名称").fill("锻件");
  await createDlg.getByRole("button", { name: "创建" }).click();
  await page.waitForURL("**/recipes/**");

  await addPhaseFromTemplate(page, "FURNACE", "PH-HEAT");
  await labeledInput(page, "单元规程").fill("UP-固溶");
  await page.locator(".el-table__body tr").filter({ hasText: "升温时长" }).locator(".el-input-number input").first().fill("1");

  await page.getByRole("button", { name: "+ 并行单元规程" }).click();
  const parallel = page.getByRole("dialog", { name: "新增并行单元规程" });
  await expect(parallel).toBeVisible();
  await labeledInput(parallel, "单元规程").fill("UP-淬火");
  await parallel.getByRole("button", { name: "添加并行工步" }).click();
  await expect(parallel).toBeHidden();
  await page.locator(".el-table__body tr").filter({ hasText: "冷却时长" }).locator(".el-input-number input").first().fill("1");

  await page.getByRole("button", { name: "+ 质检" }).click();
  await labeledInput(page, "单元规程").fill("UP-QC");

  const editor = page.locator(".edge-editor");
  await expect(editor.getByText("S20 → S30")).toBeVisible();
  await page.locator(".step-item").filter({ hasText: "S30" }).click();
  await editor.getByRole("button", { name: "汇合到当前工步" }).click();
  await expect(editor.getByText("S10 → S30")).toBeVisible();
  await expect(page.locator(".lane-chip").filter({ hasText: "UP-固溶" })).toBeVisible();
  await expect(page.locator(".lane-chip").filter({ hasText: "UP-淬火" })).toBeVisible();

  await page.getByRole("button", { name: "提交审核" }).click();
  await esignReasonAndWait(page, "/procedure", "PUT", "保存工艺 · 电子签名", "ISA-88 并行 Unit Procedure", passwords["工艺工程师"]);
  await esignAndWait(page, "/submit", "POST", "提交审核 · 电子签名", passwords["工艺工程师"]);

  await loginAs(page, "工艺主管");
  await page.goto("/approvals");
  await page.getByRole("cell", { name: code, exact: true }).click();
  await page.getByRole("button", { name: "通过并电子签名" }).click();
  await esignReasonAndWait(page, "/decide", "POST", "当前节点：工艺主管", "并行单元可绑定不同 PLC", passwords["工艺主管"]);

  await loginAs(page, "质量工程师");
  await page.goto("/approvals");
  await page.getByRole("cell", { name: code, exact: true }).click();
  await page.getByRole("button", { name: "通过并电子签名" }).click();
  await esignReasonAndWait(page, "/decide", "POST", "当前节点：质量", "汇合质检不写 PLC", passwords["质量工程师"]);

  await loginAs(page, "车间操作员");
  await createBatchFromApproved(page, batchNo, code, "HT-01", { "UP-淬火": "HT-02" });
  await expect(page.getByText(/UP-固溶→HT-01|UP-淬火→HT-02/)).toBeVisible();

  await page.getByRole("button", { name: "启动执行" }).click();
  await esignAndWait(page, "/start", "POST", "启动批次", passwords["车间操作员"]);
  await expect(page.locator(".page-title")).toContainText("· 待放行 ·", { timeout: 90_000 });
  await expect(page.locator(".page-title")).toContainText("HT-01:ReadyToAdvance");
  await expect(page.locator(".page-title")).toContainText("HT-02:ReadyToAdvance");
  await expect(page.getByText("UP-固溶 · HT-01")).toBeVisible();
  await expect(page.getByText("UP-淬火 · HT-02")).toBeVisible();

  const rows = await handshakeRows(page);
  expect(rows.some((r) => r.stepCode === "S10" && r.kind === "write")).toBeTruthy();
  expect(rows.some((r) => r.stepCode === "S20" && r.kind === "write")).toBeTruthy();
  expect(rows.some((r) => r.stepCode === "S30" && r.kind === "write")).toBeFalsy();
  expect(rows.some((r) => r.stepCode === "S30" && r.kind === "quality" && (r.detail ?? "").includes("禁止写 PLC"))).toBeTruthy();
  expect(rows.some((r) => (r.detail ?? "").includes("HT-01"))).toBeTruthy();
  expect(rows.some((r) => (r.detail ?? "").includes("HT-02"))).toBeTruthy();
});
