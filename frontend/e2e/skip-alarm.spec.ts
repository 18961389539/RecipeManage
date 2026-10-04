import { expect, type Page } from "@playwright/test";
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

// 注意：这里传的是 mode 值（NoAck/CorruptEcho/…），不是下拉菜单里的中文标签——
// 标签过过 t()，拿它当 mode 会被后端 400。标签↔mode 的映射抄自设备页 injectModes。
const INJECT_MODE_LABELS: Record<string, string> = {
  HoldNotReady: "保持未 Ready（禁止写参）",
  CorruptEcho: "回读不一致（拒绝 Trigger_Write）",
  NoAck: "Trigger 后不应答",
  StepError: "PLC 报 Step_Error",
  DropHeartbeat: "丢失心跳",
  None: "清除故障"
};

async function injectSimulatorFault(page: Page, equipmentCode: string, mode: string) {
  await page.goto("/equipment");
  const row = page.getByRole("row")
    .filter({ has: page.getByRole("cell", { name: equipmentCode, exact: true }) });
  await row.getByRole("button", { name: "仿真故障" }).hover();
  const pending = page.waitForResponse(
    (r) => r.request().method() === "POST" && r.url().includes("/inject-fault")
  );
  await page.getByRole("menuitem", { name: INJECT_MODE_LABELS[mode] }).click();
  // 设备页 inject() 会先弹确认框（"向 HT-01 注入…？"），点「注入」才真正调 /inject-fault。
  await page.getByRole("button", { name: INJECT_MODE_LABELS[mode] === "清除故障" ? "清除" : "注入" }).click();
  expect((await pending).ok(), `inject-fault ${mode} 被后端拒绝`).toBeTruthy();
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

test("supervisor skips ManualConfirm without writing PLC", async ({ page }) => {
  const health = await page.request.get("/health");
  expect(health.ok()).toBeTruthy();
  expect((await health.json()).controlRecipe).toBe("TEXT");
  await abortActiveBatches(page.request);

  const batchNo = `BSKP${uniqueStamp()}`;
  await loginAs(page, "车间操作员");
  await createBatchFromApproved(page, batchNo, "AL-HT-CFM", "HT-01");
  await page.getByRole("button", { name: "EN", exact: true }).click();
  await expect(page.getByRole("button", { name: "Start execution", exact: true })).toBeVisible();
  await page.getByRole("button", { name: "中文", exact: true }).click();
  await page.getByRole("button", { name: "启动执行" }).click();
  await esignAndWait(page, "/start", "POST", "启动批次", passwords["车间操作员"]);
  await expect(page.locator(".handshake-status").filter({ hasText: "等待人工确认" }).first()).toBeVisible({ timeout: 45_000 });
  const batchUrl = page.url();

  await loginAs(page, "工艺主管");
  await page.goto(batchUrl);
  await page.getByRole("button", { name: "EN", exact: true }).click();
  await expect(page.getByRole("button", { name: "Skip current step", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Hold", exact: true })).toBeVisible();
  await page.getByRole("button", { name: "中文", exact: true }).click();
  await expect(page.getByRole("button", { name: "跳过当前工步" })).toBeVisible();
  await page.getByRole("button", { name: "跳过当前工步" }).click();
  await esignReasonAndWait(page, "/skip", "POST", "跳过当前工步（仅 PLC_Ready / 等待 / 人工确认，且未写参）", "E2E 跳过人工确认", passwords["工艺主管"]);
  await expect(page.locator(".page-title")).toContainText("· 待放行 ·", { timeout: 30_000 });

  const rows = await handshakeRows(page);
  expect(rows.some((r) => r.stepCode === "S10" && r.kind === "write")).toBeTruthy();
  expect(rows.some((r) => r.stepCode === "S20" && r.kind === "write")).toBeFalsy();
  expect(rows.some((r) => r.stepCode === "S20" && r.kind === "skip")).toBeTruthy();
});

test("NoAck handshake fault raises alarm and operator can acknowledge", async ({ page }) => {
  await abortActiveBatches(page.request);
  const batchNo = `BALM${uniqueStamp()}`;
  await loginAs(page, "车间操作员");
  await injectSimulatorFault(page, "HT-01", "NoAck");

  try {
    await createBatchFromApproved(page, batchNo, "AL-HT-CFM", "HT-01");
    await page.getByRole("button", { name: "启动执行" }).click();
    await esignAndWait(page, "/start", "POST", "启动批次", passwords["车间操作员"]);
    await expect(page.locator(".batch-meta-item").nth(1)).toHaveText("故障", { timeout: 45_000 });
    const batchUrl = page.url();
    await page.goto("/alarms");
    const pendingRow = page.locator(".alarm-table .el-table__body tr").filter({ hasText: batchNo });
    await expect(pendingRow).toBeVisible();
    await expect(pendingRow).toHaveClass(/alarm-pending/);
    await page.goto(batchUrl);
    // 报警表在监控页的「报警」页签里，默认停在「工步」页签：不先切页签，卡片是 display:none。
    await page.getByRole("tab").filter({ hasText: "报警" }).click();
    const alarmCard = page.locator(".el-card").filter({ has: page.locator(".el-card__header", { hasText: "过程报警" }) });
    await expect(alarmCard).toBeVisible();
    await expect(alarmCard).toContainText(/AckTimeout|未收到 Step_Running/);

    const pendingAck = page.waitForResponse(
      (r) => r.request().method() === "POST" && /\/api\/alarms\/[^/]+\/ack/.test(new URL(r.url()).pathname)
    );
    await alarmCard.getByRole("button", { name: "确认", exact: true }).click();
    expect((await pendingAck).ok()).toBeTruthy();
    // exact 才能把页头的「确认全部 N 条」排除掉：getByRole 的 name 默认是子串匹配，
    // 不写 exact 会同时命中两个按钮并触发 strict mode 违规。
    await expect(alarmCard.getByRole("button", { name: "确认", exact: true })).toHaveCount(0);
    await expect(alarmCard.locator(".el-table__body")).toContainText("车间操作员");

    await page.goto("/alarms");
    await page.locator(".filter-bar .el-radio-button").filter({ hasText: "全部" }).click();
    const alarmRow = page.locator(".el-table__body").getByRole("row").filter({ hasText: batchNo });
    await expect(alarmRow).not.toHaveClass(/alarm-pending/);
    await expect(alarmRow).toContainText("车间操作员");
    await expect(alarmRow).toContainText(/AckTimeout/);
  } finally {
    await clearSimulatorFault(page.request, "HT-01");
  }
});
