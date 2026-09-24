import { expect, test } from "@playwright/test";
import {
  fillPrompt,
  labeledInput,
  loginAs,
  uniqueStamp,
} from "./helpers";

test.describe.configure({ mode: "serial" });

test("material genealogy split charge binds eBR and lab sample is not a PLC trend tag", async ({ page }) => {
  const health = await page.request.get("/health");
  expect(health.ok()).toBeTruthy();
  expect((await health.json()).controlRecipe).toBe("TEXT");

  const stamp = uniqueStamp();
  const parentLot = `INGOT-${stamp}`;
  const childLot = `${parentLot}-S1`;
  const batchNo = `BLOT${stamp}`;
  const sampleCode = `QC-${stamp}`;

  await loginAs(page, "车间操作员");
  await page.goto("/lots");
  await expect(page.getByRole("heading", { name: "物料批次谱系" })).toBeVisible();
  await page.getByRole("button", { name: "登记来料批" }).click();
  const receive = page.getByRole("dialog", { name: "登记来料批" });
  await labeledInput(receive, "批次号").fill(parentLot);
  await labeledInput(receive, "物料编码").fill("AL6061");
  await labeledInput(receive, "物料名称").fill("铝合金锭");
  await receive.getByRole("button", { name: "登记" }).click();
  await expect(page.getByRole("cell", { name: parentLot, exact: true })).toBeVisible();

  await page.getByRole("cell", { name: parentLot, exact: true }).click();
  await expect(page.getByRole("heading", { name: new RegExp(`谱系 · ${parentLot}`) })).toBeVisible();
  await page.getByRole("button", { name: "拆分子批" }).click();
  const split = page.getByRole("dialog", { name: "拆分子批" });
  await labeledInput(split, "子批号").fill(childLot);
  await split.getByRole("button", { name: "拆分" }).click();
  await expect(page.getByRole("heading", { name: new RegExp(`谱系 · ${childLot}`) })).toBeVisible();

  await page.goto("/batches");
  await page.getByRole("button", { name: "从已批准配方创建" }).click();
  const dialog = page.getByRole("dialog", { name: "创建批次 / 生成控制配方快照" });
  await labeledInput(dialog, "批次号").fill(batchNo);
  await dialog.locator(".el-select").nth(0).click();
  await page.getByRole("option", { name: /AL-HT-CFM/ }).click();
  await dialog.locator(".el-select").nth(1).click();
  const idle = page.getByRole("option", { name: /^HT-01 .*空闲/ });
  if (await idle.count())
    await idle.first().click();
  else
    await page.getByRole("option").filter({ hasText: /^HT-01 / }).first().click();
  await labeledInput(dialog, "物料批次").fill(`PROD-${stamp}`);
  await dialog.locator(".el-form-item").filter({ hasText: "投料批" }).locator(".el-select").click();
  await page.getByRole("option", { name: new RegExp(childLot) }).click();
  await page.keyboard.press("Escape");
  await dialog.getByRole("button", { name: "生成快照并创建" }).click();
  await page.waitForURL(/\/batches\/[0-9a-f-]+$/i);
  await page.getByRole("button", { name: "电子批记录" }).click();
  await expect(page.getByRole("heading", { name: /电子批记录/ })).toBeVisible();
  const materialTable = page.locator("section.block").filter({ has: page.getByRole("heading", { name: "物料投料与产出谱系" }) });
  await expect(materialTable.getByRole("cell", { name: childLot, exact: true })).toBeVisible();
  await expect(materialTable.getByRole("cell", { name: `PROD-${stamp}`, exact: true })).toBeVisible();
  await expect(materialTable.getByText("投料", { exact: true })).toBeVisible();
  await expect(materialTable.getByText("产出", { exact: true })).toBeVisible();

  await page.getByRole("button", { name: "取样" }).click();
  await fillPrompt(page, "实验室取样", sampleCode);
  await expect(page.getByRole("cell", { name: sampleCode, exact: true })).toBeVisible();
  await expect(page.getByText("终检")).toBeVisible();
});
