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
import { i18n, t } from "./i18n";
import { reportSessionExpired } from "./api/http";
import { useAuthStore } from "./stores/auth";
import "./styles.css";

document.documentElement.classList.add("dark");

const app = createApp(App);
app.use(createPinia());
app.use(router);
app.use(i18n);
// 这里只给个初值；真正随语言切换的是 App.vue 上的 el-config-provider。
app.use(ElementPlus, { locale: zhCn });

// 会话过期：留在应用内跳转，不整页重载——location.assign 会连用户填了一半的表单一起丢掉。
// 必须说明原因，否则莫名其妙回到登录页只会被当成系统故障。
reportSessionExpired((from) => {
  useAuthStore().logout();
  ElMessage.warning({
    // 会话过期是脚本层弹的（不在任何组件里），所以走 t() 而不是模板的 $t。
    message: t("登录状态已失效，请重新登录后继续操作。"),
    duration: 6000
  });
  void router.replace({ path: "/login", query: { redirect: from } });
});

app.mount("#app");
