import { expect, type Locator, type Page } from "@playwright/test";

export const passwords: Record<string, string> = {
  工艺工程师: "Engineer@123",
  工艺主管: "Supervisor@123",
  质量工程师: "Quality@123",
  车间操作员: "Operator@123",
  管理员: "Admin@123"
};

export async function loginAs(page: Page, role: keyof typeof passwords) {
  await page.goto("/dashboard");
  const logout = page.getByRole("button", { name: "退出" });
  if (await logout.isVisible().catch(() => false)) {
    await logout.click();
    await page.waitForURL("**/login");
  } else {
    await page.goto("/login");
  }
  await page.getByRole("button", { name: role, exact: true }).click();
  await page.getByRole("button", { name: "登录" }).click();
  await page.waitForURL("**/dashboard");
  await expect(page.getByRole("button", { name: "退出" })).toBeVisible();
}

export function uniqueStamp() {
  return `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`.toUpperCase();
}

export function labeledInput(scope: Locator, label: string) {
  return scope.getByRole("textbox", { name: label, exact: true });
}

export async function fillPrompt(page: Page, title: string, value: string) {
  const box = page.getByRole("dialog", { name: title, exact: true }).last();
  await expect(box).toBeVisible();
  const handle = await box.elementHandle();
  const input = box.locator(".el-message-box__input input, .el-message-box__input textarea");
  await input.click();
  await input.fill(value);
  await expect(input).toHaveValue(value);
  await box.getByRole("button", { name: "确定" }).click();
  if (handle)
    await handle.waitForElementState("hidden");
}

export async function esignAndWait(
  page: Page,
  urlPart: string,
  method: "POST" | "PUT",
  title: string,
  value: string
) {
  const pending = page.waitForResponse(
    (r) => r.request().method() === method && r.url().includes(urlPart)
  );
  await fillPrompt(page, title, value);
  const resp = await pending;
  expect(resp.ok(), `${method} ${urlPart} ${resp.status()}`).toBeTruthy();
  return resp;
}

export async function createBatchFromApproved(
  page: Page,
  batchNo: string,
  recipeCode: string,
  equipmentPrefix: string,
  unitBindings?: Record<string, string>,
  opts?: { allowOccupied?: boolean; scaleFactor?: number; lotNumber?: string }
) {
  await page.goto("/batches");
  await page.getByRole("button", { name: "从已批准配方创建" }).click();
  const dialog = page.getByRole("dialog", { name: "创建批次 / 生成控制配方快照" });
  await expect(dialog).toBeVisible();
  await labeledInput(dialog, "批次号").fill(batchNo);
  await dialog.locator(".el-select").nth(0).click();
  await page.getByRole("option", { name: new RegExp(recipeCode) }).click();
  await dialog.locator(".el-select").nth(1).click();
  const preferredIdle = page.getByRole("option", { name: new RegExp(`^${equipmentPrefix} .*空闲`) });
  if (await preferredIdle.count())
    await preferredIdle.click();
  else if (opts?.allowOccupied)
    await page.getByRole("option", { name: new RegExp(`^${equipmentPrefix} `) }).click();
  else
    await page.getByRole("option", { name: /Simulator · 空闲/ }).first().click();
  for (const [unit, prefix] of Object.entries(unitBindings ?? {})) {
    const row = dialog.locator(".unit-bind").filter({ hasText: unit });
    await expect(row).toBeVisible();
    const already = await row.locator(".el-select").innerText();
    if (already.includes(prefix) && already.includes("空闲"))
      continue;
    await row.locator(".el-select").click();
    const list = page.locator(".el-select-dropdown:visible").last();
    const idle = list.getByRole("option", { name: new RegExp(`^${prefix} .*空闲`) });
    if (await idle.count())
      await idle.click();
    else
      await list.getByRole("option", { name: /Simulator · 空闲/ }).first().click();
  }
  if (opts?.scaleFactor != null) {
    const scale = dialog.locator(".el-form-item").filter({ hasText: "缩放因子" }).locator("input");
    await scale.fill("");
    await scale.fill(String(opts.scaleFactor));
  }
  if (opts?.lotNumber)
    await labeledInput(dialog, "物料批次").fill(opts.lotNumber);
  const pending = page.waitForResponse(
    (r) => r.request().method() === "POST" && /\/api\/batches\/?$/.test(new URL(r.url()).pathname)
  );
  await dialog.getByRole("button", { name: "生成快照并创建" }).click();
  const resp = await pending;
  expect(resp.ok(), `POST /batches ${resp.status()} ${await resp.text()}`).toBeTruthy();
  await page.waitForURL(/\/batches\/[0-9a-f-]{36}/i);
  await expect(page.getByText("快照 完整性有效")).toBeVisible();
}

export async function handshakeRows(page: Page) {
  const token = await page.evaluate(() => localStorage.getItem("rm_token"));
  const id = page.url().split("/batches/")[1]?.split(/[?#]/)[0];
  const log = await page.request.get(`/api/batches/${id}/handshake-log`, {
    headers: { Authorization: `Bearer ${token}` }
  });
  expect(log.ok(), `handshake-log ${log.status()}`).toBeTruthy();
  return log.json() as Promise<{ stepCode: string; kind: string; detail?: string | null }[]>;
}

export async function abortActiveBatches(request: Page["request"]) {
  const login = await request.post("/api/auth/login", {
    data: { userName: "operator", password: "Operator@123" }
  });
  expect(login.ok()).toBeTruthy();
  const token = (await login.json() as { token: string }).token;
  const headers = { Authorization: `Bearer ${token}` };
  const batches = await request.get("/api/batches", { headers });
  const items = (await batches.json()) as { id: string; status: string }[];
  for (const b of items.filter((x) => ["Running", "Held", "Queued"].includes(x.status))) {
    await request.post(`/api/batches/${b.id}/abort`, {
      headers,
      data: { password: "Operator@123", reason: "e2e cleanup" }
    });
  }
  const deadline = Date.now() + 10_000;
  while (Date.now() < deadline) {
    const eq = await request.get("/api/equipment", { headers });
    const lines = (await eq.json()) as { occupancy?: string }[];
    if (lines.every((e) => e.occupancy !== "Occupied"))
      return;
    await new Promise((r) => setTimeout(r, 300));
  }
}
