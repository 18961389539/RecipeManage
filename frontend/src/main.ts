import { createApp } from "vue";
import { createPinia } from "pinia";
import ElementPlus from "element-plus";
import zhCn from "element-plus/es/locale/lang/zh-cn";
import { ElMessage } from "element-plus";
import "element-plus/dist/index.css";
// 本应用是深色 UI，但此前未启用 Element Plus 暗色主题，
// 导致 el-card / el-table / el-input / el-dialog 以默认浅色（白底）浮在深色背景上。
// 先加载官方暗色变量，再由 styles.css 里的 html.dark 覆盖为项目色板。
import "element-plus/theme-chalk/dark/css-vars.css";
import App from "./App.vue";
import router from "./router";
import { reportSessionExpired } from "./api/http";
import { useAuthStore } from "./stores/auth";
import "./styles.css";

document.documentElement.classList.add("dark");

const app = createApp(App);
app.use(createPinia());
app.use(router);
app.use(ElementPlus, { locale: zhCn });

// 会话过期：留在应用内跳转，不整页重载——location.assign 会连用户填了一半的表单一起丢掉。
// 必须说明原因，否则莫名其妙回到登录页只会被当成系统故障。
reportSessionExpired((from) => {
  useAuthStore().logout();
  ElMessage.warning({
    message: "登录状态已失效，请重新登录后继续操作。",
    duration: 6000
  });
  void router.replace({ path: "/login", query: { redirect: from } });
});

app.mount("#app");
