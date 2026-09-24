import { readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { expect, test } from "@playwright/test";
import {
  loginAs,
  uniqueStamp,
} from "./helpers";

test.describe.configure({ mode: "serial" });

test("recipe JSON export import clones as draft without covering existing codes", async ({ page }) => {
  const health = await page.request.get("/health");
  expect(health.ok()).toBeTruthy();
  expect((await health.json()).controlRecipe).toBe("TEXT");

  const code = `IMP${uniqueStamp()}`;
  await loginAs(page, "工艺工程师");
  await page.goto("/recipes");

  const downloadPromise = page.waitForEvent("download");
  await page.getByRole("button", { name: "导出 JSON" }).click();
  const download = await downloadPromise;
  const raw = JSON.parse(await readFile(await download.path(), "utf8")) as {
    recipes: { code: string; name: string; approved?: unknown; draft?: unknown; versions?: unknown[] }[];
  };
  expect(raw.recipes.length).toBeGreaterThan(0);
  const clone = {
    ...raw,
    recipes: [{ ...raw.recipes[0], code, name: "E2E导入克隆" }]
  };
  const file = join(tmpdir(), `brmes-${code}.json`);
  await writeFile(file, JSON.stringify(clone), "utf8");

  await page.locator('input[type="file"]').setInputFiles(file);
  await expect(page.locator(".el-message").filter({ hasText: /新建 1/ })).toBeVisible();
  await expect(page.getByRole("cell", { name: code, exact: true })).toBeVisible();
  const row = page.getByRole("row").filter({ has: page.getByRole("cell", { name: code, exact: true }) });
  await expect(row.getByRole("cell", { name: "草稿", exact: true })).toBeVisible();
});

test("admin backup on PostgreSQL offers recipe JSON not sqlite file", async ({ page }) => {
  await loginAs(page, "管理员");
  await page.goto("/users");
  await expect(page.getByRole("heading", { name: "用户与备份" })).toBeVisible();
  await expect(page.getByText(/当前为 PostgreSQL/)).toBeVisible();
  await expect(page.getByRole("button", { name: "导出配方 JSON" })).toBeVisible();
  await expect(page.getByRole("button", { name: "下载 SQLite" })).toHaveCount(0);
});
