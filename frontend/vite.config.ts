import { defineConfig } from "vitest/config";
import vue from "@vitejs/plugin-vue";

export default defineConfig({
  plugins: [vue()],
  test: {
    // 纯逻辑测试跑 node 环境即可；将来要给组件写测试再按需引 happy-dom。
    environment: "node",
    include: ["src/**/*.spec.ts"]
  },
  build: {
    // 产物直接落到 API 的 wwwroot：出包时 dotnet publish 会带着它一起进 zip，现场一个进程、一个端口
    // （开发时前端仍走 vite dev + 代理，这个目录不存在也不影响）。该目录在 .gitignore 里，不入库。
    outDir: "../src/RecipesManage.Api/wwwroot",
    emptyOutDir: true,
    rollupOptions: {
      output: {
        // 只固定四个体积大、且整包同进同出的依赖组：命名稳定 → 业务代码改动不再让 vendor 的
        // 内容哈希一起失效。其余交给默认算法，避免把只被单个懒加载路由用到的库并进公共 chunk。
        manualChunks(id) {
          if (!id.includes("node_modules")) return;
          const p = id.replace(/\\/g, "/");
          if (/\/node_modules\/(echarts|zrender)\//.test(p)) return "vendor-echarts";
          if (/\/node_modules\/(element-plus|@element-plus)\//.test(p)) return "vendor-element-plus";
          if (/\/node_modules\/uplot\//.test(p)) return "vendor-uplot";
          if (/\/node_modules\/@microsoft\/signalr\//.test(p)) return "vendor-signalr";
        }
      }
    }
  },
  server: {
    port: 5173,
    proxy: {
      "/api": { target: "http://localhost:5010", changeOrigin: true },
      "/hubs": { target: "http://localhost:5010", changeOrigin: true, ws: true },
      "/health": { target: "http://localhost:5010", changeOrigin: true }
    }
  }
});
