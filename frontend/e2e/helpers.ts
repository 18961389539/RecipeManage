import { expect, test as base, type Locator, type Page } from "@playwright/test";

export const passwords: Record<string, string> = {
  工艺工程师: "Engineer@123",
  工艺主管: "Supervisor@123",
  质量工程师: "Quality@123",
  车间操作员: "Operator@123",
  管理员: "Admin@123"
};

export async function loginAs(page: Page, role: keyof typeof passwords) {
  await page.goto("/dashboard");
  // 顶栏的「退出」会先弹确认框（AppShell.logout 里的 ElMessageBox），
  // 而确认框的主按钮也叫「退出」——所以点的时候限定在 .user 里，确认的时候限定在弹框里，
  // 少了这一步，任何二次登录的用例都会卡在 waitForURL("**/login")。
  const logout = page.locator(".user button", { hasText: "退出" });
  if (await logout.isVisible().catch(() => false)) {
    await logout.click();
    const box = page.locator(".el-message-box");
    await expect(box).toBeVisible();
    await box.locator(".el-message-box__btns .el-button--primary").click();
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

/**
 * 按表单标签取输入框。
 *
 * 别用 `exact: true`：Element Plus 把必填星号算进可访问名，`编码` 实际叫 `"* 编码"`，
 * 精确匹配会全部落空（实测 dialog.getByRole("textbox", { name: "编码", exact: true }) → 0）。
 * 用锚定的正则匹配"整名 = 可选星号 + 标签"，既能命中必填项，也不会把 `编码` 误配到 `产品编码`。
 */
export function labeledInput(scope: Locator, label: string) {
  const escaped = label.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  return scope.getByRole("textbox", { name: new RegExp(`^(\\* )?${escaped}$`) });
}

/**
 * 定位消息弹框。
 *
 * 别再回到 `getByRole("dialog", { name, exact: true })`：签名弹窗的标题现在统一拼成
 * 「动作 · 电子签名」（utils/esign.ts 的 esignTitle），精确匹配一个都对不上；
 * 而 ElMessageBox 的 role 在不同版本里 dialog/alertdialog 混用过。按弹框内文本过滤最稳。
 */
function msgBox(page: Page, title: string) {
  return page.locator(".el-message-box").filter({ hasText: title }).last();
}

export async function fillPrompt(page: Page, title: string, value: string) {
  const box = msgBox(page, title);
  await expect(box).toBeVisible();
  const input = box.locator(".el-message-box__input input, .el-message-box__input textarea");
  await input.click();
  await input.fill(value);
  await expect(input).toHaveValue(value);
  await box.getByRole("button", { name: "确定" }).click();
  await expect(box).toBeHidden();
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

/**
 * 「原因 + 密码」合并成一屏的签名弹窗（utils/esign.ts 的 esignWithReason）。
 *
 * 09-22 之前是 prompt(原因) → prompt(密码) 两个弹框串联，用例也就写两次；
 * 合并后只剩一个弹框、一个「签名并确认」按钮，所以必须在一个框里填完两栏再点。
 */
export async function esignReasonAndWait(
  page: Page,
  urlPart: string,
  method: "POST" | "PUT",
  title: string,
  reason: string,
  password: string
) {
  const box = msgBox(page, title);
  const form = box.locator(".esign-form");
  await expect(form).toBeVisible();
  const pending = page.waitForResponse(
    (r) => r.request().method() === method && r.url().includes(urlPart)
  );
  const reasonField = form.locator("textarea").first();
  await reasonField.fill(reason);
  await expect(reasonField).toHaveValue(reason);
  await form.locator('input[type="password"]').fill(password);
  await box.getByRole("button", { name: "签名并确认" }).click();
  const resp = await pending;
  expect(resp.ok(), `${method} ${urlPart} ${resp.status()}`).toBeTruthy();
  await expect(box).toBeHidden();
  return resp;
}

/**
 * 从相库加一个工艺相（设计器右侧调色板：选设备类 → 「从相模板添加」→ 点模板）。
 *
 * 设计器早就不提供 `+ 升温` 这类按钮了——快捷行只剩上位机工步（等待 / 质检 / 人工确认），
 * 工艺相必须由相模板生成，否则程序号与参数槽没有唯一来源。用例也跟着走这条路。
 */
export async function addPhaseFromTemplate(page: Page, classCode: string, templateCode: string) {
  const palette = page.locator(".palette");
  await palette.locator(".el-select").first().click();
  const options = page.locator(".el-select-dropdown:visible").last();
  await options.getByRole("option", { name: new RegExp(`^${classCode} ·`) }).click();
  await palette.getByRole("button", { name: "从相模板添加" }).click();
  const menu = page.locator(".el-dropdown-menu:visible").last();
  await menu.getByRole("menuitem", { name: new RegExp(templateCode) }).click();
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
  const preferredIdle = page.getByRole("option", { name: new RegExp(`^${equipmentPrefix} · 空闲`) });
  if (await preferredIdle.count())
    await preferredIdle.click();
  else if (opts?.allowOccupied)
    await page.getByRole("option", { name: new RegExp(`^${equipmentPrefix} `) }).click();
  else
    await page.getByRole("option", { name: /空闲/ }).filter({ hasNotText: "不允许" }).first().click();
  for (const [unit, prefix] of Object.entries(unitBindings ?? {})) {
    const row = dialog.locator(".unit-bind").filter({ hasText: unit });
    await expect(row).toBeVisible();
    const already = await row.locator(".el-select").innerText();
    if (already.includes(prefix) && already.includes("空闲"))
      continue;
    await row.locator(".el-select").click();
    const list = page.locator(".el-select-dropdown:visible").last();
    const idle = list.getByRole("option", { name: new RegExp(`^${prefix} · 空闲`) });
    if (await idle.count())
      await idle.click();
    else
      await list.getByRole("option", { name: /空闲/ }).filter({ hasNotText: "不允许" }).first().click();
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
  await expect(page.getByText("快照：完整性有效")).toBeVisible();
}

export async function handshakeRows(page: Page) {
  const token = await page.evaluate(() => localStorage.getItem("rm_token"));
  const id = page.url().split("/batches/")[1]?.split(/[?#]/)[0];
  const log = await page.request.get(`/api/batches/${id}/handshake-log`, {
    headers: { Authorization: `Bearer ${token}` }
  });
  expect(log.ok(), `handshake-log ${log.status()}`).toBeTruthy();
  // 端点是分页信封 { items, total }（服务端分页收口时改的），这里解出 items。
  const body = (await log.json()) as { items: { stepCode: string; kind: string; detail?: string | null }[] };
  return body.items;
}

/**
 * 失败清理夹具：用例失败/超时后，中止它遗留在途的 e2e 批次并等租约释放。
 * 没有这一层，一个失败用例留下的 Running 批次会让后续用例 START 时撞 423 Locked，
 * 失败沿文件序级联（一个用例挂掉，后面 4-5 个全跟着 423）。
 * 通过的用例不清理——它们的批次本就该是终态。
 */
export const test = base.extend({
  page: async ({ page }, use, testInfo) => {
    await use(page);
    if (testInfo.status !== testInfo.expectedStatus) {
      try { await abortActiveBatches(page.request); } catch { /* 清理失败不掩盖原失败 */ }
    }
  },
});

export async function abortActiveBatches(request: Page["request"]) {
  const login = await request.post("/api/auth/login", {
    data: { userName: "operator", password: "Operator@123" }
  });
  expect(login.ok()).toBeTruthy();
  const token = (await login.json() as { token: string }).token;
  const headers = { Authorization: `Bearer ${token}` };
  // 列表是分页的：取满一页上限再筛，别用默认 50 条漏掉在跑的批次。
  const batches = await request.get("/api/batches?take=200", { headers });
  const items = ((await batches.json()) as { items: { id: string; status: string; batchNo?: string }[] }).items;
  const live = ["Running", "Held", "Queued", "Faulted"];
  const targets = items.filter((x) => live.includes(x.status) && isE2eBatchNo(x.batchNo));
  for (const b of targets) {
    await request.post(`/api/batches/${b.id}/abort`, {
      headers,
      data: { password: "Operator@123", reason: "e2e cleanup" }
    });
  }
  const ids = new Set(targets.map((b) => b.id));
  const deadline = Date.now() + 10_000;
  while (Date.now() < deadline) {
    const again = await request.get("/api/batches?take=200", { headers });
    const rows = ((await again.json()) as { items: { id: string; status: string }[] }).items;
    if (rows.filter((x) => ids.has(x.id)).every((x) => !live.includes(x.status)))
      return;
    await new Promise((r) => setTimeout(r, 300));
  }
}

function isE2eBatchNo(batchNo?: string) {
  // 各 spec 建批用的号段前缀（与 spec 里的模板保持一致）。BCFM/BOOS 曾经漏在名单外，
  // 执行闭环与升版占用两个 spec 的批次从来没被清理过，是 423/占用级联的漏网之鱼。
  // B 后面是数字的号（如 B2026…）是真实演示批次，不能误伤，所以这里逐段列举而不是 ^B\w+。
  return !!batchNo && /^(BE2E|BOOS|BCFM|BOPS|BHLD|BSCL|BSKP|BALM|BP2U|BDRF|BMBW|BS7|BRTY|BLOT|BCLS|BA|BB)/i.test(batchNo);
}
