import { expect, test } from "@playwright/test";
import {
  labeledInput,
  loginAs,
  uniqueStamp,
} from "./helpers";

test.describe.configure({ mode: "serial" });

test("engineer applies PH-HEAT template onto a draft step", async ({ page }) => {
  const health = await page.request.get("/health");
  expect(health.ok()).toBeTruthy();
  expect((await health.json()).controlRecipe).toBe("TEXT");

  const code = `PH${uniqueStamp()}`;
  await loginAs(page, "工艺工程师");
  await page.goto("/recipes");
  await page.getByRole("button", { name: "新建配方" }).click();
  const createDlg = page.getByRole("dialog", { name: "新建主配方" });
  await labeledInput(createDlg, "编码").fill(code);
  await labeledInput(createDlg, "名称").fill("相模板升温");
  await labeledInput(createDlg, "产品编码").fill("AL6061");
  await labeledInput(createDlg, "产品名称").fill("锻件");
  await createDlg.getByRole("button", { name: "创建" }).click();
  await page.waitForURL("**/recipes/**");

  await page.getByRole("button", { name: "从相模板添加" }).click();
  await page.getByRole("menuitem", { name: /^PH-HEAT / }).click();
  await expect(page.locator(".step-item").filter({ hasText: "升温至设定点" })).toBeVisible();
  await expect(page.locator(".el-table__body tr").filter({ hasText: "目标温度" })).toBeVisible();
  await expect(page.locator(".el-table__body tr").filter({ hasText: "升温斜率" })).toBeVisible();
});

test("Mix control recipe cannot snapshot onto FURNACE-classed HT-01", async ({ page }) => {
  const batchNo = `BCLS${uniqueStamp()}`;
  await loginAs(page, "车间操作员");
  await page.goto("/batches");
  await page.getByRole("button", { name: "从已批准配方创建" }).click();
  const dialog = page.getByRole("dialog", { name: "创建批次 / 生成控制配方快照" });
  await labeledInput(dialog, "批次号").fill(batchNo);
  await dialog.locator(".el-select").nth(0).click();
  await page.getByRole("option", { name: /AL-PR-OPS/ }).click();
  await dialog.locator(".el-select").nth(1).click();
  const idle = page.getByRole("option", { name: /^HT-01 .*空闲/ });
  if (await idle.count())
    await idle.first().click();
  else
    await page.getByRole("option").filter({ hasText: /^HT-01 / }).first().click();
  const pending = page.waitForResponse(
    (r) => r.request().method() === "POST" && /\/api\/batches\/?$/.test(new URL(r.url()).pathname)
  );
  await dialog.getByRole("button", { name: "生成快照并创建" }).click();
  const resp = await pending;
  expect(resp.status()).toBe(400);
  const body = await resp.json() as { code?: string; message?: string };
  expect(body.code).toBe("EQ_CLASS");
  expect(body.message ?? "").toMatch(/Mix/);
  await expect(page.locator(".el-message").filter({ hasText: /不允许执行 Mix/ })).toBeVisible();
});

test("admin equipment list shows ISA-88 class codes", async ({ page }) => {
  await loginAs(page, "管理员");
  await page.goto("/equipment");
  await expect(page.getByRole("heading", { name: "设备与 PLC 驱动" })).toBeVisible();
  const ht01 = page.getByRole("row").filter({ has: page.getByRole("cell", { name: "HT-01", exact: true }) });
  await expect(ht01.getByRole("cell", { name: "FURNACE", exact: true })).toBeVisible();
  const ht02 = page.getByRole("row").filter({ has: page.getByRole("cell", { name: "HT-02", exact: true }) });
  await expect(ht02.getByRole("cell", { name: "QUENCH", exact: true })).toBeVisible();
  const ua01 = page.getByRole("row").filter({ has: page.getByRole("cell", { name: "UA-01", exact: true }) });
  await expect(ua01.getByRole("cell", { name: "GENERIC", exact: true })).toBeVisible();
});
