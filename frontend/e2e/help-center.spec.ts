import { expect } from "@playwright/test";
import { loginAs, test } from "./helpers";

test.describe("软件使用说明中心", () => {
  test("文章深链定位章节，搜索和分类同步筛选目录", async ({ page }) => {
    await loginAs(page, "管理员");
    await page.goto("/help/recipe-lifecycle#review");
    await expect(page.getByRole("heading", { name: "配方设计与审核" })).toBeVisible();
    await expect(page.locator("#review")).toBeVisible();
    await expect(page).toHaveURL(/\/help\/recipe-lifecycle#review$/);

    const search = page.getByRole("textbox", { name: "搜索主题、步骤或关键词" });
    await search.fill("审批链");
    await expect(page.locator(".guide-item")).toHaveCount(1);
    await expect(page.locator(".guide-item")).toContainText("配方设计与审核");

    await search.clear();
    await page.locator(".help-categories").getByRole("button", { name: /^批次执行/ }).click();
    await expect(page.locator(".guide-item")).toHaveCount(1);
    await expect(page.getByRole("heading", { name: "批次创建与执行" })).toBeVisible();
  });

  test("页面问号保留快速说明并可打开完整指南", async ({ page }) => {
    await loginAs(page, "管理员");
    await page.goto("/dashboard");
    await page.getByRole("button", { name: "本页说明" }).click();
    const dialog = page.getByRole("dialog", { name: "运行总览说明" });
    await expect(dialog).toBeVisible();
    await dialog.getByRole("link", { name: "查看完整说明" }).click();
    await expect(page).toHaveURL(/\/help\/start-here$/);
    await expect(page.getByRole("heading", { name: "先从这里开始" })).toBeVisible();
  });

  test("文章适用范围不授予权限，受限业务链接按当前角色隐藏", async ({ page }) => {
    await loginAs(page, "车间操作员");
    await page.goto("/help/alarms-troubleshooting");
    await expect(page.getByRole("heading", { name: "报警处理与追溯" })).toBeVisible();
    await expect(page.getByRole("link", { name: "过程报警", exact: true })).toBeVisible();
    await expect(page.getByRole("link", { name: "操作审计", exact: true })).toHaveCount(0);
    await page.goto("/help/accounts-backup");
    await expect(page).toHaveURL(/\/help\/start-here$/);
  });

  test("英文界面与窄屏布局可用，斜杠快捷键聚焦搜索", async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await loginAs(page, "管理员");
    await page.goto("/help");
    await page.getByRole("button", { name: "EN" }).click();
    await expect(page.getByRole("heading", { name: "Software guide" })).toBeVisible();
    const search = page.getByRole("textbox", { name: "Search topics, steps, or keywords" });
    await page.keyboard.press("/");
    await expect(search).toBeFocused();

    const layout = await page.evaluate(() => ({
      width: document.documentElement.clientWidth,
      scrollWidth: document.documentElement.scrollWidth
    }));
    expect(layout.scrollWidth).toBeLessThanOrEqual(layout.width);
  });
});
