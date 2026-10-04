import { expect } from "@playwright/test";
import { loginAs, test } from "./helpers";

test("用户与备份页适配手机视口，表格可独立横向浏览", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await loginAs(page, "管理员");
  await page.goto("/users");

  await expect(page.getByRole("heading", { name: "用户与备份" })).toBeVisible();
  await expect(page.locator(".mobile-table-hint")).toHaveCount(2);
  for (const selector of [".users-table", ".backup-files-table"]) {
    const table = page.locator(selector);
    const scroller = table.locator(".el-table__body-wrapper .el-scrollbar__wrap");
    await expect.poll(() => scroller.evaluate((el) => el.scrollWidth > el.clientWidth)).toBe(true);
    await scroller.evaluate((el) => { el.scrollLeft = el.scrollWidth; });
    await expect.poll(() => scroller.evaluate((el) => el.scrollLeft > 0)).toBe(true);
  }

  const layout = await page.evaluate(() => ({
    viewportWidth: document.documentElement.clientWidth,
    documentWidth: document.documentElement.scrollWidth
  }));
  expect(layout.documentWidth).toBeLessThanOrEqual(layout.viewportWidth);

  const descriptionRows = page.locator(".backup-card .el-descriptions__body tr");
  await expect(descriptionRows).toHaveCount(5);
  await page.getByRole("button", { name: "新建用户" }).click();
  const dialog = page.getByRole("dialog");
  await expect(dialog).toBeVisible();
  const dialogBox = await dialog.boundingBox();
  expect(dialogBox).not.toBeNull();
  expect(dialogBox!.x).toBeGreaterThanOrEqual(0);
  expect(dialogBox!.x + dialogBox!.width).toBeLessThanOrEqual(layout.viewportWidth);
  await dialog.getByRole("button", { name: "取消" }).click();
});

test("备份进行中禁用互斥操作，避免点击无反馈", async ({ page }) => {
  await loginAs(page, "管理员");
  await page.goto("/users");
  const card = page.locator(".backup-card");
  await expect(card.getByRole("button", { name: "立即备份一份" })).toBeEnabled();

  let notifyStarted!: () => void;
  const postStarted = new Promise<void>((resolve) => { notifyStarted = resolve; });
  await page.route("**/system/backups", async (route) => {
    if (route.request().method() !== "POST") {
      await route.continue();
      return;
    }
    notifyStarted();
    await new Promise((resolve) => setTimeout(resolve, 600));
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ name: "ui-test-backup.db" })
    });
  });

  await card.getByRole("button", { name: "立即备份一份" }).click();
  await postStarted;
  await expect(card.getByRole("button", { name: "立即备份一份" })).toBeDisabled();
  await expect(card.getByRole("button", { name: "立即维护" })).toBeDisabled();
  await expect(card.getByRole("button", { name: "刷新" })).toBeDisabled();
  await expect(page.getByRole("button", { name: "下载 SQLite" })).toBeDisabled();
});
