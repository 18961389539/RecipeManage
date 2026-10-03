/**
 * 分批跑全量 e2e。
 *
 * 为什么不是一次 npx playwright test：这台开发机同时跑着后端 + Vite + 本会话，
 * 52 个用例连续跑时浏览器 worker 会在 30 个上下开始挂死
 * （worker-N process did not exit within 300000ms，实测三轮一致）。
 * 分批让每批自带全新的浏览器生命周期，绕开累积效应；隔离跑本来就是可信的信号源。
 *
 * 用法：node e2e/run-batched.mjs [每批文件数，默认 3]
 */
import { readdirSync } from "node:fs";
import { spawnSync } from "node:child_process";

const batchSize = Number(process.argv[2] ?? 3);
const specs = readdirSync("e2e").filter((f) => f.endsWith(".spec.ts")).sort();
const groups = [];
for (let i = 0; i < specs.length; i += batchSize) groups.push(specs.slice(i, i + batchSize));

const failed = [];
for (let g = 0; g < groups.length; g++) {
  const group = groups[g];
  const files = group.map((f) => `e2e/${f}`);
  console.log(`\n=== 批次 ${g + 1}/${groups.length}: ${group.join(", ")} ===`);
  const r = spawnSync("npx", ["playwright", "test", ...files, "--reporter=list"], {
    stdio: "inherit",
    shell: process.platform === "win32",
    env: { ...process.env, NODE_OPTIONS: "" }
  });
  if (r.status !== 0) failed.push(...group);
}

console.log(`\n=== 汇总：${specs.length} 个 spec / ${groups.length} 批 ===`);
if (failed.length) {
  console.log("仍有失败的 spec：\n  " + failed.join("\n  "));
  process.exit(1);
}
console.log("全部批次通过 ✓");
