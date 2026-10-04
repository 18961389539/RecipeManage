/// <reference types="vite/client" />

declare module "*.vue" {
  import type { DefineComponent } from "vue";
  const component: DefineComponent<{}, {}, any>;
  export default component;
}

export {};

declare module "vue-router" {
  interface RouteMeta {
    roles?: import("./api/types").UserRole[];
    /** 本页是否订阅执行事件（顶栏实时徽标只在这类页面出现） */
    realtime?: boolean;
  }
}

declare module "vue" {
  export interface GlobalComponents {
    PageGuideButton: typeof import("./components/PageGuideButton.vue").default;
  }
}
