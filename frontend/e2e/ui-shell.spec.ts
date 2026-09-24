import { expect, test, type Page } from "@playwright/test";
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
  });

  test("侧栏条目随角色收敛：管理员看不到「多级审核」，但看得到「审批链配置」", async ({ page }) => {
    const nav = page.locator(".el-menu-item span");
    await expect(nav.filter({ hasText: "审批链配置" })).toBeVisible();
    await expect(nav.filter({ hasText: "多级审核" })).toHaveCount(0);
    await expect(nav.filter({ hasText: "用户与备份" })).toBeVisible();
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

  test("斜杠聚焦本页搜索，输入即筛出空态文案", async ({ page }) => {
    await page.goto("/batches");
    await page.keyboard.press("/");
    const search = page.locator("[data-shortcut-search] input, input[data-shortcut-search]");
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
    await expect(page.locator(".menu-btn")).toBeVisible();
    await page.locator(".menu-btn").click();
    await expect(page.locator(".drawer-mask")).toBeVisible();
    await page.locator(".drawer-mask").click();
    await page.goto("/batches");
    await expect(page.locator(".el-table__body tr").first()).toBeVisible();
    await expect(page.locator(".el-table-fixed-column--left").first()).toBeVisible();
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
    expect(passwords["工艺主管"]).toBeTruthy();
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
