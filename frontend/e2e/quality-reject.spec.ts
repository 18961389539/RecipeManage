import { expect, test } from "@playwright/test";
import { esignAndWait, fillPrompt, labeledInput, loginAs, passwords, uniqueStamp } from "./helpers";

test("quality reject then reopen and approve records audit", async ({ page }) => {
  const health = await page.request.get("/health");
  expect(health.ok()).toBeTruthy();
  expect((await health.json()).controlRecipe).toBe("jsonb");

  const code = `QAR${uniqueStamp()}`;
  await loginAs(page, "工艺工程师");
  await page.goto("/recipes");
  await page.getByRole("button", { name: "新建配方" }).click();
  const createDlg = page.getByRole("dialog", { name: "新建主配方" });
  await labeledInput(createDlg, "编码").fill(code);
  await labeledInput(createDlg, "名称").fill("E2E质量驳回");
  await labeledInput(createDlg, "产品编码").fill("AL6061");
  await labeledInput(createDlg, "产品名称").fill("锻件");
  await createDlg.getByRole("button", { name: "创建" }).click();
  await page.waitForURL("**/recipes/**");

  await page.getByRole("button", { name: "+ Heat" }).click();
  await page.locator(".el-table__body tr").filter({ hasText: "升温时长" }).locator(".el-input-number input").first().fill("1");
  await page.getByRole("button", { name: "+ QualityCheck" }).click();
  await page.getByRole("button", { name: "提交审核" }).click();
  await fillPrompt(page, "保存工艺 · 变更控制", "提交质量审核");
  await esignAndWait(page, "/procedure", "PUT", "保存工艺 · 电子签名", passwords["工艺工程师"]);
  await esignAndWait(page, "/submit", "POST", "提交审核 · 电子签名", passwords["工艺工程师"]);

  await loginAs(page, "工艺主管");
  await page.goto("/approvals");
  await page.getByRole("cell", { name: code, exact: true }).click();
  await page.getByRole("button", { name: "通过并电子签名" }).click();
  await fillPrompt(page, "当前节点：工艺主管", "路径可执行，交质量");
  await esignAndWait(page, "/decide", "POST", "电子签名", passwords["工艺主管"]);

  await loginAs(page, "质量工程师");
  await page.goto("/approvals");
  await page.getByRole("cell", { name: code, exact: true }).click();
  await page.getByRole("button", { name: "驳回" }).click();
  await fillPrompt(page, "当前节点：质量", "窗口需返工");
  await esignAndWait(page, "/decide", "POST", "电子签名", passwords["质量工程师"]);

  await page.goto("/audit");
  await page.locator(".el-select").click();
  await page.getByRole("option", { name: "主配方" }).click();
  await page.getByRole("button", { name: "刷新" }).click();
  await expect(page.locator(".el-table__body")).toContainText("recipe.decide.esign");
  await expect(page.locator(".el-table__body")).toContainText(/Quality:Rejected/);

  await loginAs(page, "工艺工程师");
  await page.goto("/recipes");
  await expect(page.getByRole("row").filter({ has: page.getByRole("cell", { name: code, exact: true }) }).getByRole("cell", { name: "驳回", exact: true })).toBeVisible();
  await page.getByRole("cell", { name: code, exact: true }).click();
  await page.getByRole("button", { name: "重新打开" }).click();
  await esignAndWait(page, "/reopen", "POST", "重新打开驳回版本 · 电子签名", passwords["工艺工程师"]);
  await page.getByRole("button", { name: "提交审核" }).click();
  await fillPrompt(page, "保存工艺 · 变更控制", "质量驳回后重开");
  await esignAndWait(page, "/procedure", "PUT", "保存工艺 · 电子签名", passwords["工艺工程师"]);
  await esignAndWait(page, "/submit", "POST", "提交审核 · 电子签名", passwords["工艺工程师"]);

  await loginAs(page, "工艺主管");
  await page.goto("/approvals");
  await page.getByRole("cell", { name: code, exact: true }).click();
  await page.getByRole("button", { name: "通过并电子签名" }).click();
  await fillPrompt(page, "当前节点：工艺主管", "返工后路径可执行");
  await esignAndWait(page, "/decide", "POST", "电子签名", passwords["工艺主管"]);

  await loginAs(page, "质量工程师");
  await page.goto("/approvals");
  await page.getByRole("cell", { name: code, exact: true }).click();
  await page.getByRole("button", { name: "通过并电子签名" }).click();
  await fillPrompt(page, "当前节点：质量", "窗口合格");
  await esignAndWait(page, "/decide", "POST", "电子签名", passwords["质量工程师"]);

  await loginAs(page, "工艺工程师");
  await page.goto("/recipes");
  await expect(page.getByRole("row").filter({ has: page.getByRole("cell", { name: code, exact: true }) }).getByRole("cell", { name: "1", exact: true })).toBeVisible();
});
