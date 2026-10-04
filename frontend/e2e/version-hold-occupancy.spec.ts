import { expect, type Page } from "@playwright/test";
import { test } from "./helpers";
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

test("version bump hold occupancy and resume without blind write", async ({ page }) => {
  const health = await page.request.get("/health");
  expect(health.ok()).toBeTruthy();
  expect((await health.json()).controlRecipe).toBe("TEXT");
  await abortActiveBatches(page.request);

  const stamp = uniqueStamp();
  const code = `VHO${stamp}`;
  const batchA = `BA${stamp}`;
  const batchB = `BB${stamp}`;

  await loginAs(page, "工艺工程师");
  await page.goto("/recipes");
  await page.getByRole("button", { name: "新建配方" }).click();
  const createDlg = page.getByRole("dialog", { name: "新建主配方" });
  await labeledInput(createDlg, "编码").fill(code);
  await labeledInput(createDlg, "名称").fill("E2E升版保持占用");
  await labeledInput(createDlg, "产品编码").fill("AL6061");
  await labeledInput(createDlg, "产品名称").fill("锻件");
  await createDlg.getByRole("button", { name: "创建" }).click();
  await page.waitForURL("**/recipes/**");

  await addPhaseFromTemplate(page, "FURNACE", "PH-HEAT");
  await page.locator(".el-table__body tr").filter({ hasText: "升温时长" }).locator(".el-input-number input").first().fill("1");
  await page.getByRole("button", { name: "+ 等待" }).click();
  await page.locator(".el-table__body tr").filter({ hasText: "等待时长" }).locator(".el-input-number input").first().fill("12");

  await page.getByRole("button", { name: "提交审核" }).click();
  await esignReasonAndWait(page, "/procedure", "PUT", "保存工艺 · 电子签名", "v1 Heat+Wait", passwords["工艺工程师"]);
  await esignAndWait(page, "/submit", "POST", "提交审核 · 电子签名", passwords["工艺工程师"]);
  await approveSubmitted(page, code, "v1 路径可执行", "v1 窗口合格");

  await loginAs(page, "工艺工程师");
  await page.goto("/recipes");
  await page.getByRole("cell", { name: code, exact: true }).click();
  await page.waitForURL("**/recipes/**");
  await page.getByRole("button", { name: "升版" }).click();
  await esignReasonAndWait(page, "/new-version", "POST", "升版 · 电子签名", "缩短等待时长并升版", passwords["工艺工程师"]);
  await expect(page.locator(".ver.on")).toContainText("v2");
  await expect(page.getByRole("button", { name: "提交审核" })).toBeVisible();

  await page.locator(".step-item").filter({ hasText: "S20" }).click();
  await page.locator(".el-table__body tr").filter({ hasText: "等待时长" }).locator(".el-input-number input").first().fill("8");
  await page.getByRole("button", { name: "提交审核" }).click();
  await esignReasonAndWait(page, "/procedure", "PUT", "保存工艺 · 电子签名", "v2 等待 8s", passwords["工艺工程师"]);
  await esignAndWait(page, "/submit", "POST", "提交审核 · 电子签名", passwords["工艺工程师"]);

  await loginAs(page, "工艺主管");
  await page.goto("/approvals");
  await page.getByRole("textbox", { name: "搜索编码 / 名称 / 产品" }).fill(code);
  await expect(page.getByRole("cell", { name: code, exact: true })).toBeVisible();
  await expect(page.locator(".head b")).toBeVisible();
  await expect(page.locator(".el-loading-mask")).toHaveCount(0);
  await expect(page.locator(".diff-block")).toBeVisible();
  let compareRequests = 0;
  let allowCompareSuccess = false;
  const compareRoute = /\/compare/;
  await page.route(compareRoute, async (route) => {
    compareRequests += 1;
    if (!allowCompareSuccess) {
      await route.fulfill({
        status: 503,
        contentType: "application/json",
        body: JSON.stringify({ message: "temporary compare outage" })
      });
    } else {
      await route.continue();
    }
  });
  await page.getByRole("cell", { name: code, exact: true }).click();
  await expect.poll(() => compareRequests).toBeGreaterThan(0);
  await expect(page.getByRole("alert").filter({ hasText: "版本差异加载失败" })).toBeVisible();
  const approveButton = page.getByRole("button", { name: "通过并电子签名" });
  await expect(approveButton).toBeDisabled();
  await page.keyboard.press("Control+Enter");
  await expect(page.locator(".el-message-box")).toHaveCount(0);
  const retryButton = page.getByRole("button", { name: "重试加载差异" });
  await expect(retryButton).toBeVisible();
  const requestCountBeforeRetry = compareRequests;
  allowCompareSuccess = true;
  await retryButton.click();
  await expect.poll(() => compareRequests).toBeGreaterThan(requestCountBeforeRetry);
  await expect(page.getByRole("heading", { name: /相对生效版 v1 的差异/ })).toBeVisible();
  await expect(approveButton).toBeEnabled();
  await page.unroute(compareRoute);
  await expect(page.locator(".diff-block")).toContainText("S20.parameters[0].setpoint");
  await expect(page.locator(".diff-block")).toContainText("12");
  await expect(page.locator(".diff-block")).toContainText("8");
  await page.getByRole("button", { name: "通过并电子签名" }).click();
  await esignReasonAndWait(page, "/decide", "POST", "当前节点：工艺主管", "升版差异已审阅", passwords["工艺主管"]);

  await loginAs(page, "质量工程师");
  await page.goto("/approvals");
  await page.getByRole("cell", { name: code, exact: true }).click();
  await page.getByRole("button", { name: "通过并电子签名" }).click();
  await esignReasonAndWait(page, "/decide", "POST", "当前节点：质量", "v2 放行", passwords["质量工程师"]);

  await loginAs(page, "工艺工程师");
  await page.goto("/recipes");
  await page.getByRole("cell", { name: code, exact: true }).click();
  await page.getByRole("button", { name: "版本对比" }).click();
  const diff = page.getByRole("dialog", { name: "版本差异" });
  await expect(diff).toBeVisible();
  await expect(diff.getByText("v1 → v2")).toBeVisible();
  await expect(diff).toContainText("S20.parameters[0].setpoint");
  await expect(diff).toContainText("12");
  await expect(diff).toContainText("8");
  await page.keyboard.press("Escape");
  await expect(diff).toBeHidden();

  await loginAs(page, "车间操作员");
  await createBatchFromApproved(page, batchA, code, "HT-01");
  await expect(page.locator(".page-title")).toContainText("v2");

  await page.getByRole("button", { name: "启动执行" }).click();
  await esignAndWait(page, "/start", "POST", "启动批次", passwords["车间操作员"]);
  await expect(page.locator(".page-title")).toContainText("等待主控", { timeout: 45_000 });
  const firstUrl = page.url();

  await page.getByRole("button", { name: "保持" }).click();
  await esignReasonAndWait(page, "/hold", "POST", "保持批次（写 Host_Hold，等待 PLC_Held，禁止盲写）", "E2E 保持验证剩余时长", passwords["车间操作员"]);
  await expect(page.locator(".page-title")).toContainText("· 保持 ·", { timeout: 30_000 });

  await page.goto("/dashboard");
  const occRow = page.getByRole("row")
    .filter({ has: page.getByRole("cell", { name: "HT-01", exact: true }) })
    // 协议列显示的是 labels.ts 的中文标签（Simulator → 仿真器），不是原始枚举名。
    .filter({ has: page.getByRole("cell", { name: "仿真器", exact: true }) });
  await expect(occRow.getByRole("cell", { name: "占用", exact: true })).toBeVisible();
  await expect(occRow.getByRole("cell", { name: batchA, exact: true })).toBeVisible();

  await createBatchFromApproved(page, batchB, code, "HT-01", undefined, { allowOccupied: true });
  await page.getByRole("button", { name: "启动执行" }).click();
  const startB = page.waitForResponse(
    (r) => r.request().method() === "POST" && r.url().includes("/start")
  );
  await fillPrompt(page, "启动批次", passwords["车间操作员"]);
  const startResp = await startB;
  // 设备被占用是"重试可能成功"的冲突，不是参数错：产品把它映射成 423 Locked（见 ExceptionHandlingMiddleware）。
  expect(startResp.status(), await startResp.text()).toBe(423);
  expect((await startResp.json() as { code: string }).code).toBe("EQ_BUSY");
  await expect(page.locator(".el-message").filter({ hasText: /绑定设备已有批次/ })).toBeVisible();
  await expect(page.locator(".page-title")).toContainText("· 已创建 ·");

  await page.goto(firstUrl);
  await page.getByRole("button", { name: "恢复执行" }).click();
  await esignAndWait(page, "/resume", "POST", "恢复执行", passwords["车间操作员"]);
  await expect(page.locator(".page-title")).toContainText("· 待放行 ·", { timeout: 45_000 });

  const rows = await handshakeRows(page);
  expect(rows.some((r) => r.stepCode === "S10" && r.kind === "write")).toBeTruthy();
  expect(rows.some((r) => r.stepCode === "S20" && r.kind === "write")).toBeFalsy();
  expect(rows.some((r) => r.kind === "hold")).toBeTruthy();
  expect(rows.some((r) => r.stepCode === "S20" && r.kind === "wait")).toBeTruthy();
});
