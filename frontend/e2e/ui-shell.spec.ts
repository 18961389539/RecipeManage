import { expect, type Page } from "@playwright/test";
import { test } from "./helpers";
import {
  loginAs,
  passwords,
} from "./helpers";

/**
 * 全站壳层与界面语言的 UI 自动化。
 *
 * 与既有业务闭环用例的分工：brmes-loop / parallel-units 那批跑的是"批次能不能走完四步握手"，
 * 会创建、放行甚至中止批次；**这一支一律只读**——不点任何写接口按钮，
 * 因此可以单独跑（`npx playwright test ui-shell`）而不影响共享开发库上正在跑的批次。
 *
 * 覆盖的是三轮界面改造（i18n 全量、eBR 拆组件、混排插值）最容易悄悄退化的地方：
 * 标题的唯一来源、语言切换与持久化、带占位的句子、后端消息随 Accept-Language、
 * 打印态该藏的交互件、以及窄屏下的宽表。
 */

async function apiGet<T>(page: Page, url: string): Promise<T> {
  const token = await page.evaluate(() => localStorage.getItem("rm_token"));
  const res = await page.request.get(url, { headers: { Authorization: `Bearer ${token}` } });
  expect(res.ok(), `GET ${url} → ${res.status()}`).toBeTruthy();
  return await res.json() as T;
}

type BatchRow = { id: string; batchNo: string; status: string };
type RecipeRow = { id: string; code: string; name: string };

/** 列表接口是分页信封；取满一页上限，别让断言只看得到前 50 条。 */
async function batchRows(page: Page): Promise<BatchRow[]> {
  const body = await apiGet<{ total: number; items: BatchRow[] }>(page, "/api/batches?take=200");
  return body.items;
}

test.describe("全站壳层", () => {
  test.beforeEach(async ({ page }) => { await loginAs(page, "管理员"); });

  test("顶栏与浏览器标签标题都取自路由 meta，不出现第二份名单", async ({ page }) => {
    await expect(page.locator(".head-left .section")).toHaveText("运行总览");
    await expect(page).toHaveTitle(/^运行总览 · BRMES 工艺配方管理$/);
    await page.goto("/audit");
    await expect(page.locator(".head-left .section")).toHaveText("操作审计");
    await expect(page).toHaveTitle(/^操作审计 · BRMES 工艺配方管理$/);

    const batches = await batchRows(page);
    expect(batches.length).toBeGreaterThan(0);
    await page.goto(`/batches/${batches[0].id}`);
    await expect(page.locator(".el-menu-item.is-active")).toContainText("批次执行");
  });

  test("侧栏条目随角色收敛：管理员看不到「多级审核」，但看得到「审批链配置」", async ({ page }) => {
    const nav = page.locator(".el-menu-item span");
    await expect(nav.filter({ hasText: "审批链配置" })).toBeVisible();
    await expect(nav.filter({ hasText: "多级审核" })).toHaveCount(0);
    await expect(nav.filter({ hasText: "用户与备份" })).toBeVisible();
  });

  test("审批链台在窄视口堆叠，链条可键盘选择", async ({ page }) => {
    await page.setViewportSize({ width: 1024, height: 850 });
    await page.goto("/approval-chains");
    const listColumn = page.locator(".chain-list-col");
    const editorColumn = page.locator(".chain-editor-col");
    await expect(listColumn).toBeVisible();
    await expect(editorColumn).toBeVisible();
    const [listWidth, editorWidth] = await Promise.all([
      listColumn.evaluate((el) => el.getBoundingClientRect().width),
      editorColumn.evaluate((el) => el.getBoundingClientRect().width)
    ]);
    expect(Math.abs(listWidth - editorWidth)).toBeLessThan(1);
    await expect(page.locator(".chain-status").getByText("已启用").first()).toBeVisible();

    const standardChain = page.locator(".chain-select").filter({ hasText: "标准三级" });
    await standardChain.focus();
    await page.keyboard.press("Space");
    await expect(page.getByText("编辑 standard")).toBeVisible();
    await expect(page.getByRole("button", { name: "保存并电子签名" })).toBeDisabled();

    await page.setViewportSize({ width: 390, height: 844 });
    await expect(page.locator(".steps-table").locator("xpath=preceding-sibling::p[contains(@class,'mobile-table-hint')]")).toBeVisible();
    const stepScroll = page.locator(".steps-table .el-table__body-wrapper .el-scrollbar__wrap");
    await expect.poll(() => stepScroll.evaluate((el) => el.scrollWidth > el.clientWidth)).toBe(true);
  });

  test("配方设计窄视口纵向展开，参数表可横向浏览", async ({ page }) => {
    await page.setViewportSize({ width: 1024, height: 850 });
    const recipes = await apiGet<RecipeRow[]>(page, "/api/recipes");
    expect(recipes.length).toBeGreaterThan(0);
    await page.goto(`/recipes/${recipes[0].id}`);
    await expect(page.locator(".palette")).toHaveCount(0);
    await expect(page.getByRole("button", { name: "管理相库", exact: true })).toBeVisible();

    const columns = [
      page.locator(".step-list-col"),
      page.locator(".designer-flow-col"),
      page.locator(".step-inspector-col")
    ];
    const widths = await Promise.all(columns.map((column) =>
      column.evaluate((el) => el.getBoundingClientRect().width)
    ));
    expect(Math.max(...widths) - Math.min(...widths)).toBeLessThan(1);
    await expect(page.locator(".step-fields-form")).toBeVisible();
    expect(await page.locator(".step-fields-form").evaluate((el) =>
      getComputedStyle(el).gridTemplateColumns.split(" ").length
    )).toBe(2);

    const secondStep = page.locator(".step-item").nth(1);
    await secondStep.focus();
    await page.keyboard.press("Space");
    await expect(secondStep).toHaveAttribute("aria-pressed", "true");

    await page.setViewportSize({ width: 390, height: 844 });
    await expect(page.locator(".parameter-table").locator("xpath=preceding-sibling::p[contains(@class,'mobile-table-hint')]")).toBeVisible();
    const parameterScroll = page.locator(".parameter-table .el-table__body-wrapper .el-scrollbar__wrap");
    await expect.poll(() => parameterScroll.evaluate((el) => el.scrollWidth > el.clientWidth)).toBe(true);
  });

  test("未知地址落到 404 兜底页，且保留侧栏与回跳入口", async ({ page }) => {
    await page.goto("/definitely-not-a-page");
    await expect(page.getByRole("main").getByText("页面不存在")).toBeVisible();
    await expect(page.locator(".el-menu-item")).not.toHaveCount(0);
    await expect(page.getByRole("button", { name: "回到运行总览" })).toBeVisible();
    await page.getByRole("button", { name: "回到运行总览" }).click();
    await expect(page).toHaveURL(/\/dashboard$/);
  });

  test("越权访问跳回总览并说明原因，而不是静默换页", async ({ page }) => {
    await loginAs(page, "车间操作员");
    await page.goto("/users");
    await expect(page).toHaveURL(/\/dashboard$/);
    await expect(page.getByText(/无权访问「用户与备份」/)).toBeVisible();

    // 批记录（归档凭据）同样按角色收敛：不渲染入口之外，直接改地址也进不去。
    await page.goto("/batches/00000000-0000-0000-0000-000000000000/record");
    await expect(page).toHaveURL(/\/dashboard$/);
    await expect(page.getByText(/无权访问「批次追溯记录」/)).toBeVisible();
  });

  test("实时徽标只在订阅 hub 的页面出现", async ({ page }) => {
    await expect(page.locator(".rt")).toBeVisible();
    await page.goto("/audit");
    await expect(page.locator(".rt")).toHaveCount(0);
    const recipes = await apiGet<RecipeRow[]>(page, "/api/recipes");
    await page.goto(`/recipes/${recipes[0].id}`);
    await expect(page.locator(".rt")).toHaveCount(0);
  });

  test("点徽标立刻重拉本页数据，不等轮询周期", async ({ page }) => {
    const badge = page.locator(".rt");
    await expect(badge).toBeVisible();
    const [resp] = await Promise.all([
      page.waitForResponse(r => r.url().includes("/api/dashboard") && r.status() === 200),
      badge.click()
    ]);
    expect(resp.ok()).toBeTruthy();
  });

  test("会话过期时提示重新登录并带回原地址，而不是硬刷新", async ({ page }) => {
    await page.route("**/api/dashboard", route =>
      route.request().method() === "GET"
        ? route.fulfill({ status: 401, contentType: "application/json", body: "{\"code\":\"AUTH\"}" })
        : route.continue());
    await page.reload();
    await expect(page.getByText("登录状态已失效，请重新登录后继续操作。")).toBeVisible();
    await expect(page).toHaveURL(/\/login\?redirect=\/dashboard/);
    await page.unroute("**/api/dashboard");
  });
});

test.describe("界面语言", () => {
  test("默认中文；切到英文后壳层、表头、KPI 与计数一起翻，且刷新后保持", async ({ page }) => {
    await loginAs(page, "管理员");
    await expect(page.locator(".head-left .section")).toHaveText("运行总览");

    await page.getByRole("button", { name: "EN", exact: true }).click();
    await expect(page.locator(".head-left .section")).toHaveText("Overview");
    await expect(page.locator(".el-menu-item span").filter({ hasText: "Master recipes" })).toBeVisible();
    await expect(page.getByText("Handshake-faulted batches")).toBeVisible();

    await page.goto("/batches");
    await expect(page.locator(".el-table__header th .cell").filter({ hasText: "Batch no." }).first()).toBeVisible();
    // 带占位的句子：两侧都得把数字带出来，不能露出 {0}
    const count = page.locator(".result-count");
    await expect(count).toContainText(/items$/);
    await expect(count).not.toContainText("{0}");

    await page.reload();
    await expect(page.locator(".head-left .section")).toHaveText("Batch execution");
    await expect(page.getByRole("button", { name: "中文", exact: true })).toBeVisible();

    await page.getByRole("button", { name: "中文", exact: true }).click();
    await expect(page.locator(".head-left .section")).toHaveText("批次执行态");
    await expect(page.locator(".result-count")).toContainText(/条$/);
  });

  test("后端提示随 Accept-Language：英文界面下 404 文案是英文", async ({ page }) => {
    await loginAs(page, "管理员");
    await page.getByRole("button", { name: "EN", exact: true }).click();
    await page.goto("/batches/00000000-0000-0000-0000-000000000000/record");
    await expect(page.getByText(/Failed to load the batch record: Batch not found\./)).toBeVisible();
  });

  test("术语悬停在英文界面给英文解释", async ({ page }) => {
    await loginAs(page, "管理员");
    await page.getByRole("button", { name: "EN", exact: true }).click();
    await page.goto("/dashboard");
    const tip = page.locator(".help-tip").filter({ hasText: "Four-step handshake" }).first();
    await expect(tip).toBeVisible();
    await tip.hover();
    const popper = page.locator(".help-tip-pop").last();
    await expect(popper).toBeVisible();
    await expect(popper).toContainText("handshake between the host and the PLC");
    await expect(popper).not.toContainText(/[一-鿿]/);
  });

  test("快捷键面板：? 打开、Esc 关闭，行名与键位提示都是英文", async ({ page }) => {
    await loginAs(page, "管理员");
    await page.getByRole("button", { name: "EN", exact: true }).click();
    await page.goto("/batches");
    await page.keyboard.press("Shift+Slash");
    const dialog = page.getByRole("dialog", { name: "Shortcuts" });
    await expect(dialog).toBeVisible();
    await expect(dialog.getByText("Global")).toBeVisible();
    await expect(dialog.getByText("Focus this page's search")).toBeVisible();
    // 键位提示行由 chordHint 拼：占位必须被替换成实际按键，否则界面直接显示 {0}
    await expect(dialog.locator(".help-tip-plain").first()).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(dialog).toBeHidden();
  });
});

test.describe("列表交互约定", () => {
  test.beforeEach(async ({ page }) => { await loginAs(page, "管理员"); });

  test("表头排序走 EP 内置 aria-sort，点一次翻转", async ({ page }) => {
    await page.goto("/batches");
    const th = page.locator(".el-table__header th.is-sortable").filter({ hasText: "批次号" });
    await th.click();
    await expect(th).toHaveAttribute("aria-sort", "ascending");
    await th.click();
    await expect(th).toHaveAttribute("aria-sort", "descending");
  });

  test("表格行可键盘到达，Enter 打开详情", async ({ page }) => {
    await page.goto("/batches");
    const row = page.locator(".el-table__body tr").first();
    await expect(row).toHaveAttribute("tabindex", "0");
    await row.focus();
    await page.keyboard.press("Enter");
    await expect(page).toHaveURL(/\/batches\/[0-9a-f-]{36}$/);
  });

  test("批次监控在手机视口下摘要可换行、操作按钮不溢出且可触控", async ({ page }) => {
    const rows = await batchRows(page);
    expect(rows.length).toBeGreaterThan(0);
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(`/batches/${rows[0].id}`);

    const metadata = page.locator(".batch-meta");
    const actions = page.locator(".batch-actions");
    await expect(metadata).toBeVisible();
    await expect(actions).toBeVisible();
    await expect(actions.locator(".el-button").first()).toBeVisible();

    const layout = await page.evaluate(() => ({
      viewportWidth: document.documentElement.clientWidth,
      documentWidth: document.documentElement.scrollWidth
    }));
    const [metadataBox, actionsBox] = await Promise.all([
      metadata.boundingBox(),
      actions.boundingBox()
    ]);
    expect(layout.documentWidth).toBeLessThanOrEqual(layout.viewportWidth);
    expect(metadataBox).not.toBeNull();
    expect(actionsBox).not.toBeNull();
    expect(metadataBox!.x + metadataBox!.width).toBeLessThanOrEqual(layout.viewportWidth + 1);
    expect(actionsBox!.x + actionsBox!.width).toBeLessThanOrEqual(layout.viewportWidth + 1);

    const buttonHeights = await actions.locator(".el-button").evaluateAll((buttons) =>
      buttons.map((button) => button.getBoundingClientRect().height)
    );
    expect(buttonHeights.length).toBeGreaterThan(0);
    expect(Math.min(...buttonHeights)).toBeGreaterThanOrEqual(36);
  });

  test("Dashboard KPI 键盘聚焦有悬停反馈，Space 可打开对应筛选", async ({ page }) => {
    await page.goto("/dashboard");
    const kpi = page.getByRole("button", { name: /握手故障批次/ });
    await expect(kpi).toBeVisible();

    for (let i = 0; i < 30 && !(await kpi.evaluate((el) => el === document.activeElement)); i++) {
      await page.keyboard.press("Tab");
    }
    await expect(kpi).toBeFocused();
    await expect(kpi).toHaveCSS("background-color", "rgb(22, 32, 58)");
    await expect(kpi).toHaveCSS("border-color", "rgb(61, 139, 253)");

    await page.keyboard.press("Space");
    await expect(page).toHaveURL(/\/batches\?status=Faulted$/);
  });

  test("斜杠聚焦本页搜索，输入即筛出空态文案", async ({ page }) => {
    await page.goto("/batches");
    // 先等搜索框渲染出来再按键：/ 的 when() 判的是"本页有没有搜索框"，
    // 在页面 chunk 落地前按下去会被判成不可用而静默跳过（实测单跑必失败，整包靠重试侥幸过）。
    const search = page.locator("[data-shortcut-search] input, input[data-shortcut-search]");
    await search.waitFor({ state: "visible" });
    await page.keyboard.press("/");
    await expect(search).toBeFocused();
    await search.fill("绝对不存在的关键字 ZZZ");
    await expect(page.getByText("没有匹配的批次")).toBeVisible();
    await expect(page.locator(".result-count")).toContainText(/0 \/ \d+ 条/);
  });

  test("排序与搜索都发给后端：列表是分页取数的，客户端排不出全量", async ({ page }) => {
    await page.goto("/batches");
    const urls: string[] = [];
    page.on("request", (r) => { if (r.url().includes("/api/batches?")) urls.push(r.url()); });

    const th = page.locator(".el-table__header th.is-sortable").filter({ hasText: "批次号" });
    await th.click();
    await expect
      .poll(() => urls.some((u) => u.includes("sort=batchNo") && u.includes("dir=asc")),
        { message: `点表头应当重新取数，实际发过：${urls.join(" ")}` })
      .toBe(true);

    // 搜索框停 300ms 才发请求：防抖要防住"每敲一个字符打一次后端"。
    const before = urls.length;
    await page.locator("[data-shortcut-search] input, input[data-shortcut-search]").fill("BE2E");
    expect(urls.length).toBeLessThan(before + 2);
    await expect.poll(() => urls.some((u) => u.includes("q=BE2E"))).toBe(true);
  });
});

test.describe("窄屏与打印", () => {
  test("768px 以下侧栏收成抽屉、宽表首列冻结并可横向滚动", async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await loginAs(page, "管理员");
    const menuButton = page.locator(".menu-btn");
    const sidebar = page.locator("#app-sidebar");
    const content = page.locator("#app-content");
    await expect(menuButton).toBeVisible();
    await expect(menuButton).toHaveAttribute("aria-expanded", "false");
    await expect(sidebar).toHaveAttribute("inert", "");
    await menuButton.click();
    await expect(menuButton).toHaveAttribute("aria-expanded", "true");
    await expect(page.locator(".drawer-mask")).toBeVisible();
    await expect(content).toHaveAttribute("inert", "");
    await expect(sidebar.locator(".el-menu-item").first()).toBeFocused();
    await page.keyboard.press("Escape");
    await expect(page.locator(".drawer-mask")).toHaveCount(0);
    await expect(menuButton).toBeFocused();
    await expect(content).not.toHaveAttribute("inert");

    await menuButton.click();
    await expect(page.locator(".drawer-mask")).toBeVisible();
    await page.locator(".drawer-mask").click();
    await expect(page.locator(".drawer-mask")).toHaveCount(0);
    await page.goto("/batches");
    await expect(page.locator(".el-table__body tr").first()).toBeVisible();
    await expect(page.locator(".el-table-fixed-column--left").first()).toBeVisible();

    await page.goto("/equipment");
    await expect(page.getByText("窄屏下左右滑动表格查看其余列和行操作。")).toBeVisible();
    const equipmentTable = page.locator(".equipment-table");
    const equipmentScroll = equipmentTable.locator(".el-table__body-wrapper .el-scrollbar__wrap");
    await expect(equipmentTable.locator(".el-table__fixed-right")).toHaveCount(0);
    await expect.poll(() => equipmentScroll.evaluate((el) => el.scrollWidth > el.clientWidth)).toBe(true);
    await equipmentScroll.evaluate((el) => { el.scrollLeft = el.scrollWidth; });
    await expect.poll(() => equipmentScroll.evaluate((el) => el.scrollLeft)).toBeGreaterThan(0);

    await page.getByRole("button", { name: "编辑" }).first().click();
    const equipmentDialog = page.getByRole("dialog");
    await expect(equipmentDialog).toBeVisible();
    const dialogBox = await equipmentDialog.boundingBox();
    expect(dialogBox).not.toBeNull();
    expect(dialogBox!.x).toBeGreaterThanOrEqual(0);
    expect(dialogBox!.x + dialogBox!.width).toBeLessThanOrEqual(390);
    await expect(page.locator(".el-form-item").filter({ hasText: "编码" }).locator("input").first()).toBeDisabled();
    const rackSlot = page.locator(".rack-slot-controls");
    await expect(rackSlot).toBeVisible();
    await expect.poll(() => rackSlot.evaluate((el) => el.scrollWidth <= el.clientWidth)).toBe(true);

    await page.getByRole("tab", { name: "写参槽" }).click();
    const parameterColumns = await page.locator(".param-grid").evaluate(
      (el) => getComputedStyle(el).gridTemplateColumns.split(/\s+/).length
    );
    expect(parameterColumns).toBe(1);
    await page.getByRole("tab", { name: "握手点表" }).click();
    await page.getByRole("button", { name: "添加实测点" }).click();
    const measuredColumns = await page.locator(".measured-list").evaluate(
      (el) => getComputedStyle(el).gridTemplateColumns.split(/\s+/).length
    );
    expect(measuredColumns).toBe(1);
    await page.getByRole("button", { name: "关闭", exact: true }).click();

    await page.goto("/alarms");
    await expect(page.getByText("窄屏下左右滑动表格查看其余列和行操作。")).toBeVisible();
    const alarmTable = page.locator(".alarm-table");
    const alarmScroll = alarmTable.locator(".el-table__body-wrapper .el-scrollbar__wrap");
    await expect(alarmTable.locator(".el-table__fixed-right")).toHaveCount(0);
    await expect.poll(() => alarmScroll.evaluate((el) => el.scrollWidth > el.clientWidth)).toBe(true);
  });

  test("电子批记录：打印态藏掉交互件，表格文字翻成纸面墨色", async ({ page }) => {
    await loginAs(page, "管理员");
    const batches = await batchRows(page);
    const done = batches.find(b => ["Completed", "Released"].includes(b.status)) ?? batches[0];
    await page.goto(`/batches/${done.id}/record`);
    await expect(page.locator(".ebr")).toBeVisible();

    const interactive = page.locator(".no-print");
    expect(await interactive.count()).toBeGreaterThan(0);
    await expect(interactive.first()).toBeVisible();

    await page.emulateMedia({ media: "print" });
    const shown = await page.evaluate(() =>
      [...document.querySelectorAll(".no-print")].filter(e => e.getClientRects().length > 0).length);
    expect(shown, "打印态仍露出交互件，会被印进归档件").toBe(0);
    const ink = await page.evaluate(() => {
      const cell = document.querySelector(".el-table .cell");
      return cell ? getComputedStyle(cell).color : "";
    });
    expect(ink).toMatch(/rgb\((17|28|85|119|120),/);
    await page.emulateMedia({ media: "screen" });
  });
});

test.describe("电子批记录的区块契约", () => {
  test("11 个归档区块齐全、列头齐、无脚本错误", async ({ page }) => {
    const errors: string[] = [];
    page.on("pageerror", e => errors.push(String(e)));
    await loginAs(page, "管理员");
    const batches = await batchRows(page);
    const done = batches.find(b => ["Completed", "Released"].includes(b.status)) ?? batches[0];
    await page.goto(`/batches/${done.id}/record`);
    await expect(page.getByRole("heading", { name: new RegExp(`电子批记录 · ${done.batchNo}`) })).toBeVisible();

    const titles = ["批次抬头", "物料投料与产出谱系", "配方电子签名", "批次执行电子签名", "过程报警",
      "ISA-88 控制配方（快照）", "实验室样品（LIMS，与 PLC 测点分开）", "归档质检",
      "四步握手时序（禁止盲写）", "快照 vs 当前生效主配方"];
    for (const t of titles)
      await expect(page.locator(".ebr-block h3").filter({ hasText: t })).toBeVisible();

    // 抬头 13 个字段：拆组件后最容易漏的就是这里
    const labels = page.locator(".el-descriptions__label");
    for (const l of ["批次号", "状态", "快照完整性", "主配方", "版本", "冻结时间", "产品",
      "缩放因子", "物料批次", "放行人", "放行时间", "放行意见", "单元设备"])
      await expect(labels.filter({ hasText: l }).first()).toBeVisible();

    await expect(page.locator(".el-table").filter({ hasText: "签署含义" })).not.toHaveCount(0);
    expect(errors, `页面脚本错误：${errors.join(" | ")}`).toEqual([]);
  });
});

test.describe("职责分离（只读断言）", () => {
  test("放行/拒收只对质量账号出现，管理员在批记录页看不到", async ({ page }) => {
    await loginAs(page, "管理员");
    const batches = await batchRows(page);
    const done = batches.find(b => b.status === "Completed") ?? batches[0];
    await page.goto(`/batches/${done.id}/record`);
    await expect(page.getByRole("button", { name: "质量放行" })).toHaveCount(0);
    await expect(page.getByRole("button", { name: "质量拒收" })).toHaveCount(0);
    await expect(page.getByRole("button", { name: "导出 PDF/A" })).toBeVisible();
  });

  test("签名框已统一：eBR 的放行是「一屏两栏」，取消不会发出请求", async ({ page }) => {
    await loginAs(page, "质量工程师");
    const batches = await batchRows(page);
    const done = batches.find(b => b.status === "Completed");
    test.skip(!done, "开发库当前没有待放行批次");
    await page.goto(`/batches/${done!.id}/record`);
    const fired: string[] = [];
    page.on("request", r => { if (r.method() === "POST") fired.push(r.url()); });

    await page.getByRole("button", { name: "质量放行" }).click();
    const boxes = page.locator(".el-message-box");
    await expect(boxes).toHaveCount(1, "放行应当只有一个弹框，不能再「先填意见、再签密码」");
    const box = boxes.first();
    await expect(box.locator(".esign-meaning")).toBeVisible();
    await expect(box.locator(".esign-hint")).toBeVisible();
    await expect(box.locator(".esign-form textarea")).toBeVisible();
    await expect(box.locator('.esign-form input[type="password"]')).toBeVisible();
    await expect(box.locator(".el-message-box__title")).toHaveText("质量放行 · 电子签名");

    await box.getByRole("button", { name: "取消" }).click();
    await expect(boxes).toHaveCount(0);
    expect(fired.filter(u => u.includes("/release"))).toEqual([]);
  });

  test("审核台对操作员不可见，对主管可见（入口按角色收敛）", async ({ page }) => {
    await loginAs(page, "车间操作员");
    await expect(page.locator(".el-menu-item span").filter({ hasText: "多级审核" })).toHaveCount(0);
    await loginAs(page, "工艺主管");
    await expect(page.locator(".el-menu-item span").filter({ hasText: "多级审核" })).toBeVisible();
    // 全局审计日志只对质量与管理员开放（audit.view），主管的菜单里不再有它。
    await expect(page.locator(".el-menu-item span").filter({ hasText: "操作审计" })).toHaveCount(0);
    expect(passwords["工艺主管"]).toBeTruthy();
  });
});

test.describe("运行总览的失败态（只读断言）", () => {
  test.beforeEach(async ({ page }) => { await loginAs(page, "管理员"); });

  /**
   * 这一组用 page.route 把 GET /api/dashboard 打成 503：只拦读请求，不写库，
   * 所以仍然符合本套件的只读约定。它守的是最危险的一种失效——数据断供却长得像"一切正常"。
   */
  const killDashboard = (page: Page) =>
    page.route("**/api/dashboard", (r) =>
      r.fulfill({ status: 503, contentType: "text/plain", body: "down" }));

  test("一次都没取到数据时：计数画「—」而不是 0，空态不谎报「没有设备」", async ({ page }) => {
    await killDashboard(page);
    await page.goto("/dashboard");

    const tiles = page.locator(".kpi-grid .kpi-value");
    // 管理员没有「多级审核」权限：待审核配方磁贴按角色过滤后不渲染，6 张变 5 张。
    await expect(tiles).toHaveCount(5);
    await expect(tiles).toHaveText(["—", "—", "—", "—", "—"]);
    await expect(page.locator(".ref-strip .ref-item b")).toHaveText(["—", "—", "—", "—"]);

    await expect(page.getByText("运行数据取数失败：后端返回 503")).toBeVisible();
    await expect(page.getByText("还没有取到任何数据")).toBeVisible();
    // 探针 /api/health 仍然 200，但屏幕上一个数都没有：徽标必须跟着数据改口，不能说"服务正常"。
    await expect(page.locator(".health-pill")).toContainText("数据中断");
    // 设备其实有 6 台，写"暂无设备数据"就是把故障说成空库。
    await expect(page.locator(".el-table__empty-text")).toHaveText(["数据不可用", "数据不可用"]);
    // 冷启动就断供时顶栏也不能写「实时」：lastSyncAt 一直是 0，旧逻辑把"从未同步"当成"不陈旧"。
    await expect(page.locator(".rt")).toContainText("刷新停滞", { timeout: 20000 });
  });

  test("取数成功时数字照常显示，且与接口一致（防止「—」变成常态）", async ({ page }) => {
    const dash = await apiGet<{ pendingReleaseBatches: number; faultedBatches: number; pendingLabBatches: number }>(page, "/api/dashboard");
    await page.goto("/dashboard");
    const tile = (label: string) =>
      page.locator(".kpi").filter({ hasText: label }).locator(".kpi-num");
    await expect(tile("待质量放行")).toHaveText(String(dash.pendingReleaseBatches));
    await expect(tile("握手故障批次")).toHaveText(String(dash.faultedBatches));
    // 磁贴点进去就是这条筛选后的列表，两个数必须相等：一个批次可能挂多个待判终样，
    // 磁贴若数样品行就会比列表条数大（DashboardCountTests 在服务端钉同一件事，这里是界面侧的出口）。
    const labList = await apiGet<{ total: number }>(page, "/api/batches?take=200&onlyLabPending=true");
    await expect(tile("待检终样")).toHaveText(String(labList.total));
    // 有积压时磁贴要带老化副文字（"最久积压 2d"）：数字只会变大，老化才知道该不该急。
    if (dash.pendingReleaseBatches > 0)
      await expect(page.locator(".kpi").filter({ hasText: "待质量放行" }).locator(".kpi-sub"))
        .toContainText(/最久积压 \d+[dh]/);
    await expect(page.locator(".health-pill")).toContainText("服务正常");
    await expect(page.locator(".el-table__body tr").first()).toBeVisible();
  });

  test("中途断供：保留最后已知数字，同时顶栏与徽标都要说「已经不新鲜」", async ({ page }) => {
    await page.goto("/dashboard");
    await expect(page.locator(".kpi-grid .kpi-value").first()).toHaveText(/\d/);

    await killDashboard(page);
    // 下一个轮询周期（4 秒）内徽标就该改口。
    await expect(page.locator(".health-pill")).toContainText("数据中断", { timeout: 15000 });
    // 根因断言：/health 一直 200，顶栏的"实时"必须由**数据**新鲜度决定而不是由探针决定。
    // 连丢三个轮询周期才转黄，所以这里给到 20 秒。
    await expect(page.locator(".rt")).toContainText("刷新停滞", { timeout: 20000 });
    // 已经拿到过的数字不能被抹成 0 或「—」：那是最后已知状态，配着"不新鲜"的提示才是有用信息。
    await expect(page.locator(".kpi-grid .kpi-value").first()).toHaveText(/\d/);
    await expect(page.getByText("下面的数字是最后一次成功取数的结果")).toBeVisible();
  });
});

test.describe("版本与库结构水位（只读）", () => {
  /**
   * 远程支持的第一句话是"你装的是哪版"。这两条钉的是同一条链的两端：探针把 version / migration /
   * pendingMigrations 带回来，界面上真的能读出来——缺任何一端，操作员都得去开终端。
   */
  test("探针带版本、最后一条已应用迁移与待应用条数", async ({ page }) => {
    const res = await page.request.get("/health");
    expect(res.ok(), `/health → ${res.status()}`).toBeTruthy();
    const body = await res.json();
    expect(typeof body.version).toBe("string");
    expect(body.version.length, "version 不能是空串").toBeGreaterThan(0);
    expect(typeof body.migration).toBe("string");
    expect(body.pendingMigrations, "开机就 Migrate，非 0 意味着升级半途失败").toBe(0);
    // 这几项是既有看门狗契约的一部分，不能因为加字段而丢。
    expect(body.database).toBe("sqlite");
    expect(body.pid).toBeGreaterThan(0);
  });

  test("运行总览的徽标悬停里念得出版本", async ({ page }) => {
    await loginAs(page, "管理员");
    const body = await (await page.request.get("/health")).json();
    await page.goto("/dashboard");
    await expect(page.locator(".health-pill")).toContainText("服务正常");

    await page.locator(".health-pill").hover();
    // 全站有很多 HelpTip 的 popper，按内容选而不是按数量选，才不会撞 Playwright 的 strict mode。
    const tip = page.locator(".help-tip-pop", { hasText: body.version as string });
    await expect(tip).toBeVisible();
    await expect(tip).toContainText(body.migration as string);
  });
});

test.describe("取数口径要写在界面上（只读断言）", () => {
  test("趋势卡声明本次是抽稀还是全量，不让人把曲线当履历读", async ({ page }) => {
    await loginAs(page, "管理员");
    // 开发库里多数批次没有样本，逐个问前几批（问全表要打几十次接口）。
    const rows = (await batchRows(page)).slice(0, 8);
    let picked: BatchRow | null = null;
    for (const row of rows) {
      const series = await apiGet<{ total: number }>(page, `/api/batches/${row.id}/samples?maxPoints=50`);
      if (series.total > 0) { picked = row; break; }
    }
    test.skip(!picked, "开发库当前没有带过程样本的批次");
    await page.goto(`/batches/${picked!.id}`);
    await page.getByRole("tab", { name: /趋势/ }).click();
    const note = page.locator(".trend-note");
    await expect(note).toBeVisible({ message: "取数口径必须注明" });
    await expect(note).toContainText(/样本/);
    await expect(note).not.toContainText("{0");
  });

  test("备份卡片报出计划与留存量，缺备份时要喊出来", async ({ page }) => {
    const errors: string[] = [];
    page.on("pageerror", e => errors.push(String(e)));
    await loginAs(page, "管理员");
    await page.goto("/users");
    const card = page.locator(".backup-card");
    await expect(card).toBeVisible();
    await expect(card.locator(".el-descriptions__label").filter({ hasText: "每天 (UTC)" })).toBeVisible();
    await expect(card.getByText(/^\d{2}:\d{2}$/)).toBeVisible();
    await expect(card.getByRole("button", { name: "立即备份一份" })).toBeVisible();
    // 没落下任何一份快照时必须显式告警——静默的"看起来一切正常"正是备份最危险的失效方式。
    const emptyWarn = card.getByText(/还没有落下任何一份备份/);
    if (await card.locator(".el-table__row").count() === 0)
      await expect(emptyWarn).toBeVisible();
    await expect(card.locator(".el-descriptions__label").filter({ hasText: "保留份数" })).toBeVisible();
    // 值在配对的 content 单元格里（label 那一格只有标题），保留份数是个光秃秃的数字。
    await expect(card.locator(".el-descriptions__content").filter({ hasText: /^\d+$/ }).first()).toBeVisible();
    expect(errors, `页面脚本错误：${errors.join(" | ")}`).toEqual([]);
  });
});
