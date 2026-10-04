import { expect } from "@playwright/test";
import { test } from "./helpers";
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
  const lineageColumns = page.locator(".lineage-map .lineage-column");
  await expect(lineageColumns).toHaveCount(3);
  await page.setViewportSize({ width: 390, height: 844 });
  await expect.poll(() => page.locator(".lineage-map").evaluate((el) =>
    getComputedStyle(el).gridTemplateColumns.trim().split(/\s+/).length
  )).toBe(1);
  await expect(page.locator(".mobile-table-hint")).toBeVisible();
  const useScroll = page.locator(".genealogy-uses-table .el-table__body-wrapper .el-scrollbar__wrap");
  await expect.poll(() => useScroll.evaluate((el) => el.scrollWidth > el.clientWidth)).toBe(true);
  await useScroll.evaluate((el) => { el.scrollLeft = el.scrollWidth; });
  await expect.poll(() => useScroll.evaluate((el) => el.scrollLeft > 0)).toBe(true);
  await page.setViewportSize({ width: 994, height: 718 });

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
  const batchId = page.url().match(/\/batches\/([0-9a-f-]+)/i)![1];

  // 操作员在监控页取样（批记录已不对车间开放）：服务端把未指定物料批的样品默认绑到本批产出批。
  await page.getByRole("button", { name: "取样" }).click();
  await fillPrompt(page, "实验室取样", sampleCode);

  // 谱系核对换质量打开归档件：物料投料/产出与 LIMS 样品都记在批记录上。
  await loginAs(page, "质量工程师");
  await page.goto(`/batches/${batchId}/record`);
  await expect(page.getByRole("heading", { name: /电子批记录/ })).toBeVisible();
  const materialTable = page.locator("section.ebr-block").filter({ has: page.getByRole("heading", { name: "物料投料与产出谱系" }) });
  await expect(materialTable.getByRole("cell", { name: childLot, exact: true })).toBeVisible();
  await expect(materialTable.getByRole("cell", { name: `PROD-${stamp}`, exact: true })).toBeVisible();
  await expect(materialTable.getByText("投料", { exact: true })).toBeVisible();
  await expect(materialTable.getByText("产出", { exact: true })).toBeVisible();

  await expect(page.getByRole("cell", { name: sampleCode, exact: true })).toBeVisible();
  await expect(page.getByText("终检")).toBeVisible();
});
