import { expect, test, type Page } from "@playwright/test";
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

test("supervisor skips ManualConfirm without writing PLC", async ({ page }) => {
  const health = await page.request.get("/health");
  expect(health.ok()).toBeTruthy();
  expect((await health.json()).controlRecipe).toBe("TEXT");
  await abortActiveBatches(page.request);

  const batchNo = `BSKP${uniqueStamp()}`;
  await loginAs(page, "车间操作员");
  await createBatchFromApproved(page, batchNo, "AL-HT-CFM", "HT-01");
  await page.getByRole("button", { name: "启动执行" }).click();
  await esignAndWait(page, "/start", "POST", "启动批次", passwords["车间操作员"]);
  await expect(page.getByText("等待人工确认")).toBeVisible({ timeout: 45_000 });
  const batchUrl = page.url();

  await loginAs(page, "工艺主管");
  await page.goto(batchUrl);
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
  await injectSimulatorFault(page, "HT-01", "Trigger 后不应答");

  try {
    await createBatchFromApproved(page, batchNo, "AL-HT-CFM", "HT-01");
    await page.getByRole("button", { name: "启动执行" }).click();
    await esignAndWait(page, "/start", "POST", "启动批次", passwords["车间操作员"]);
    await expect(page.locator(".page-title")).toContainText("· 故障 ·", { timeout: 45_000 });
    const alarmCard = page.locator(".el-card").filter({ has: page.locator(".el-card__header", { hasText: "过程报警" }) });
    await expect(alarmCard).toBeVisible();
    await expect(alarmCard).toContainText(/AckTimeout|未收到 Step_Running/);

    const pendingAck = page.waitForResponse(
      (r) => r.request().method() === "POST" && /\/api\/alarms\/[^/]+\/ack/.test(new URL(r.url()).pathname)
    );
    await alarmCard.getByRole("button", { name: "确认" }).click();
    expect((await pendingAck).ok()).toBeTruthy();
    await expect(alarmCard.getByRole("button", { name: "确认" })).toHaveCount(0);
    await expect(alarmCard.locator(".el-table__body")).toContainText("车间操作员");

    await page.goto("/alarms");
    await page.locator(".filter-bar .el-radio-button").filter({ hasText: "全部" }).click();
    const alarmRow = page.locator(".el-table__body").getByRole("row").filter({ hasText: batchNo });
    await expect(alarmRow).toContainText("车间操作员");
    await expect(alarmRow).toContainText(/AckTimeout/);
  } finally {
    await clearSimulatorFault(page.request, "HT-01");
  }
});
