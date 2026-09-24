import { expect, test, type Page } from "@playwright/test";
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

async function injectSimulatorFault(page: Page, equipmentCode: string, command: string) {
  await page.goto("/equipment");
  const row = page.getByRole("row")
    .filter({ has: page.getByRole("cell", { name: equipmentCode, exact: true }) });
  await row.getByRole("button", { name: "仿真故障" }).click();
  const pending = page.waitForResponse(
    (r) => r.request().method() === "POST" && r.url().includes("/inject-fault")
  );
  await page.getByRole("menuitem", { name: command }).click();
  expect((await pending).ok()).toBeTruthy();
}

async function clearSimulatorFault(request: Page["request"], equipmentCode: string) {
  const login = await request.post("/api/auth/login", {
    data: { userName: "operator", password: "Operator@123" }
  });
  expect(login.ok()).toBeTruthy();
  const token = (await login.json() as { token: string }).token;
  const headers = { Authorization: `Bearer ${token}` };
  const list = await request.get("/api/equipment", { headers });
  const lines = (await list.json()) as { id: string; code: string }[];
  const eq = lines.find((e) => e.code === equipmentCode);
  expect(eq, equipmentCode).toBeTruthy();
  const cleared = await request.post(`/api/equipment/${eq!.id}/inject-fault`, {
    headers,
    data: { mode: "None" }
  });
  expect(cleared.ok()).toBeTruthy();
}

test("frozen control recipe snapshot does not follow master version bump", async ({ page }) => {
  const health = await page.request.get("/health");
  expect(health.ok()).toBeTruthy();
  expect((await health.json()).controlRecipe).toBe("TEXT");
  await abortActiveBatches(page.request);

  const stamp = uniqueStamp();
  const code = `DRF${stamp}`;
  const batchNo = `BDRF${stamp}`;

  await loginAs(page, "工艺工程师");
  await page.goto("/recipes");
  await page.getByRole("button", { name: "新建配方" }).click();
  const createDlg = page.getByRole("dialog", { name: "新建主配方" });
  await labeledInput(createDlg, "编码").fill(code);
  await labeledInput(createDlg, "名称").fill("E2E快照冻结漂移");
  await labeledInput(createDlg, "产品编码").fill("AL6061");
  await labeledInput(createDlg, "产品名称").fill("锻件");
  await createDlg.getByRole("button", { name: "创建" }).click();
  await page.waitForURL("**/recipes/**");

  await addPhaseFromTemplate(page, "FURNACE", "PH-HEAT");
  await page.locator(".el-table__body tr").filter({ hasText: "升温时长" }).locator(".el-input-number input").first().fill("1");
  await page.getByRole("button", { name: "+ 质检" }).click();
  await page.getByRole("button", { name: "提交审核" }).click();
  await esignReasonAndWait(page, "/procedure", "PUT", "保存工艺 · 电子签名", "v1 Heat 1s", passwords["工艺工程师"]);
  await esignAndWait(page, "/submit", "POST", "提交审核 · 电子签名", passwords["工艺工程师"]);
  await approveSubmitted(page, code, "v1 可执行", "v1 窗口合格");

  await loginAs(page, "车间操作员");
  await createBatchFromApproved(page, batchNo, code, "HT-01");
  await expect(page.locator(".page-title")).toContainText("v1");
  const batchUrl = page.url();

  await loginAs(page, "工艺工程师");
  await page.goto("/recipes");
  await page.getByRole("cell", { name: code, exact: true }).click();
  await page.getByRole("button", { name: "升版" }).click();
  await esignReasonAndWait(page, "/new-version", "POST", "升版 · 电子签名", "拉长升温时长", passwords["工艺工程师"]);
  await page.locator(".step-item").filter({ hasText: "S10" }).click();
  await page.locator(".el-table__body tr").filter({ hasText: "升温时长" }).locator(".el-input-number input").first().fill("12");
  await page.getByRole("button", { name: "提交审核" }).click();
  await esignReasonAndWait(page, "/procedure", "PUT", "保存工艺 · 电子签名", "v2 升温 12s", passwords["工艺工程师"]);
  await esignAndWait(page, "/submit", "POST", "提交审核 · 电子签名", passwords["工艺工程师"]);
  await approveSubmitted(page, code, "升版差异已审阅", "v2 放行");

  await loginAs(page, "车间操作员");
  await page.goto(batchUrl);
  await expect(page.locator(".page-title")).toContainText("v1");
  // 漂移卡现在在「履历」页签里，el-tabs 的 pane 是懒渲染的：不点页签，DOM 里根本没有它。
  await page.getByRole("tab", { name: "履历" }).click();
  await expect(page.getByText("控制配方快照已冻结")).toBeVisible();
  const driftCard = page.locator(".el-card").filter({ hasText: "快照 vs 当前生效主配方" });
  const driftRow = driftCard.locator(".el-table__body").getByRole("row").filter({ hasText: "升温时长" });
  await expect(driftRow).toContainText("1");
  await expect(driftRow).toContainText("12");
  await expect(driftRow.getByRole("cell", { name: "是", exact: true })).toBeVisible();

  const token = await page.evaluate(() => localStorage.getItem("rm_token"));
  const id = batchUrl.split("/batches/")[1]?.split(/[?#]/)[0];
  const detail = await page.request.get(`/api/batches/${id}`, {
    headers: { Authorization: `Bearer ${token}` }
  });
  const body = await detail.json() as {
    snapshot: { versionNumber: number; steps: { parameters: { name: string; setpoint: number }[] }[] };
  };
  expect(body.snapshot.versionNumber).toBe(1);
  expect(body.snapshot.steps[0].parameters.find((p) => p.name === "升温时长")?.setpoint).toBe(1);

  await page.getByRole("button", { name: "启动执行" }).click();
  await esignAndWait(page, "/start", "POST", "启动批次", passwords["车间操作员"]);
  await expect(page.locator(".page-title")).toContainText("· 待放行 ·", { timeout: 45_000 });
  await expect(page.locator(".page-title")).toContainText("v1");
});

test("faulted handshake requeue writes again after clearing NoAck", async ({ page }) => {
  await abortActiveBatches(page.request);
  const batchNo = `BRTY${uniqueStamp()}`;
  await loginAs(page, "车间操作员");
  await injectSimulatorFault(page, "HT-01", "Trigger 后不应答");

  try {
    await createBatchFromApproved(page, batchNo, "AL-HT-CFM", "HT-01");
    await page.getByRole("button", { name: "启动执行" }).click();
    await esignAndWait(page, "/start", "POST", "启动批次", passwords["车间操作员"]);
    await expect(page.locator(".page-title")).toContainText("· 故障 ·", { timeout: 45_000 });
  } finally {
    await clearSimulatorFault(page.request, "HT-01");
  }

  await page.getByRole("button", { name: "故障后重新排队" }).click();
  await esignAndWait(page, "/start", "POST", "故障后重新排队", passwords["车间操作员"]);
  await expect(page.getByText("等待人工确认")).toBeVisible({ timeout: 45_000 });
  await page.getByRole("button", { name: "人工确认" }).click();
  await esignReasonAndWait(page, "/confirm", "POST", "人工确认本工步（禁止写 PLC）", "故障恢复后确认", passwords["车间操作员"]);
  await expect(page.locator(".page-title")).toContainText("· 待放行 ·", { timeout: 30_000 });

  const rows = await handshakeRows(page);
  expect(rows.filter((r) => r.stepCode === "S10" && r.kind === "write").length).toBeGreaterThanOrEqual(1);
  expect(rows.some((r) => r.kind === "fault")).toBeTruthy();
  expect(rows.some((r) => r.stepCode === "S20" && r.kind === "write")).toBeFalsy();
  expect(rows.some((r) => r.stepCode === "S20" && r.kind === "confirm")).toBeTruthy();
});
