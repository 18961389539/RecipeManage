import { expect } from "@playwright/test";
import { test } from "./helpers";
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
  // UI 层守卫：类不兼容的设备（Mix 配方 vs FURNACE 的 HT-01）在下拉里被置灰，选不中。
  const ht01 = page.getByRole("option").filter({ hasText: /^HT-01 / }).first();
  await expect(ht01).toHaveClass(/is-disabled/);
  await page.keyboard.press("Escape");

  // 服务端守卫单独钉：绕过被置灰的 UI 直接打 API，必须仍是 EQ_CLASS 400。
  const token = await page.evaluate(() => localStorage.getItem("rm_token"));
  const headers = { Authorization: `Bearer ${token}`, "Content-Type": "application/json" };
  const recipeRaw = (await (await page.request.get("/api/recipes", { headers })).json()) as
    { id: string; code: string }[] | { items: { id: string; code: string }[] };
  // 配方列表当前回裸数组（并非所有列表都分页了），这里两种形状都接。
  const recipes = Array.isArray(recipeRaw) ? recipeRaw : recipeRaw.items;
  const recipe = recipes.find((r) => r.code.startsWith("AL-PR-OPS"))!;
  const equipment = (await (await page.request.get("/api/equipment", { headers })).json()) as
    { id: string; code: string }[];
  const ht01Eq = equipment.find((e) => e.code === "HT-01")!;
  const resp = await page.request.post("/api/batches", {
    headers,
    data: {
      batchNo, recipeId: recipe.id, equipmentId: ht01Eq.id, scaleFactor: 1,
      lotNumber: null, unitEquipment: null
    }
  });
  expect(resp.status()).toBe(400);
  const body = (await resp.json()) as { code?: string; message?: string };
  expect(body.code).toBe("EQ_CLASS");
  // 文案引用的是设备类与单元声明的类（FURNACE vs PROCESS），不再提配方名。
  expect(body.message ?? "").toMatch(/FURNACE.*PROCESS/);
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
