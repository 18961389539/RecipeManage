import { defineConfig, devices } from "@playwright/test";

export default defineConfig({
  testDir: "./e2e",
  // 独立产物目录：默认 test-results/ 在强杀 worker 后会留下被锁的 zip/句柄，
  // 下一次运行会在产物 I/O 上挂死（实测 420s 零输出）。换目录 + 清理脚本规避。
  outputDir: "./.pw-artifacts",
  timeout: 180_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  // 整包跑在开发机上时后程负载高，个别重交互用例（下拉注入等）会偶发超时；
  // 失败重试一次，重试仍在 quieter 环境下进行，通常即可通过。隔离跑单 spec 不受影响。
  retries: 1,
  workers: 1,
  reporter: [["list"]],
  use: {
    baseURL: "http://localhost:5173",
    viewport: { width: 1440, height: 960 },
    locale: "zh-CN",
    trace: "retain-on-failure"
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
  webServer: {
    command: "npm run dev",
    url: "http://localhost:5173",
    reuseExistingServer: true,
    timeout: 60_000
  }
});
