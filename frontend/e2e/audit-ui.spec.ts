import { expect } from "@playwright/test";
import { loginAs, test } from "./helpers";

test("操作审计在手机视口下保留完整筛选与表格浏览", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await loginAs(page, "管理员");
  await page.goto("/audit");

  await expect(page.getByRole("heading", { name: "操作审计" })).toBeVisible();
  await expect(page.locator(".mobile-table-hint")).toBeVisible();
  const table = page.locator(".audit-table");
  const scroller = table.locator(".el-table__body-wrapper .el-scrollbar__wrap");
  await expect(table.locator(".el-table__fixed-right")).toHaveCount(0);
  await expect.poll(() => scroller.evaluate((el) => el.scrollWidth > el.clientWidth)).toBe(true);
  await scroller.evaluate((el) => { el.scrollLeft = el.scrollWidth; });
  await expect.poll(() => scroller.evaluate((el) => el.scrollLeft > 0)).toBe(true);

  const layout = await page.evaluate(() => ({
    viewportWidth: document.documentElement.clientWidth,
    documentWidth: document.documentElement.scrollWidth
  }));
  expect(layout.documentWidth).toBeLessThanOrEqual(layout.viewportWidth);
  const filterBox = await page.locator(".entity-filter").boundingBox();
  expect(filterBox).not.toBeNull();
  expect(filterBox!.x + filterBox!.width).toBeLessThanOrEqual(layout.viewportWidth + 1);
});
