import { expect, test } from "@playwright/test";
import { loginAs } from "./helpers";

test.describe.configure({ mode: "serial" });

test("route roles bounce unauthorized users and keep equipment for operators", async ({ page }) => {
  await loginAs(page, "工艺工程师");
  await page.goto("/users");
  await expect(page).toHaveURL(/\/dashboard/);
  await page.goto("/equipment");
  await expect(page).toHaveURL(/\/dashboard/);
  await page.goto("/approvals");
  await expect(page).toHaveURL(/\/dashboard/);

  await loginAs(page, "车间操作员");
  await page.goto("/users");
  await expect(page).toHaveURL(/\/dashboard/);
  await page.goto("/approvals");
  await expect(page).toHaveURL(/\/dashboard/);
  await page.goto("/equipment");
  await expect(page.getByRole("heading", { name: "设备与 PLC 驱动" })).toBeVisible();

  await loginAs(page, "管理员");
  await page.goto("/users");
  await expect(page.getByRole("heading", { name: "用户与备份" })).toBeVisible();
});

test("dashboard ECharts and pending-release card filter the batch list", async ({ page }) => {
  await loginAs(page, "质量工程师");
  await expect(page.getByText("执行态势（ECharts）")).toBeVisible();
  await expect(page.locator("[_echarts_instance_]").first()).toBeVisible();

  await page.getByText("待质量放行", { exact: true }).click();
  await expect(page).toHaveURL(/\/batches\?status=Completed/);
  await expect(page.getByRole("heading", { name: "生产批次" })).toBeVisible();
  await expect(page.locator(".el-radio-button.is-active")).toContainText("待放行");

  await page.locator(".el-radio-button").filter({ hasText: /^故障$/ }).click();
  await expect(page).toHaveURL(/status=Faulted/);
  await expect(page.locator(".el-radio-button.is-active")).toContainText("故障");
});
