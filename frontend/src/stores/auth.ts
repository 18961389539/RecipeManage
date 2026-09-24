import { defineStore } from "pinia";
import { computed, ref } from "vue";
import http from "../api/http";
import type { UserDto, UserRole } from "../api/types";

export const useAuthStore = defineStore("auth", () => {
  const token = ref(localStorage.getItem("rm_token") ?? "");
  const user = ref<UserDto | null>(JSON.parse(localStorage.getItem("rm_user") ?? "null"));

  const isAuthed = computed(() => !!token.value);

  async function login(userName: string, password: string) {
    const { data } = await http.post<{ token: string; user: UserDto }>("/auth/login", { userName, password });
    token.value = data.token;
    user.value = data.user;
    localStorage.setItem("rm_token", data.token);
    localStorage.setItem("rm_user", JSON.stringify(data.user));
  }

  function logout() {
    token.value = "";
    user.value = null;
    localStorage.removeItem("rm_token");
    localStorage.removeItem("rm_user");
  }

  function can(...roles: UserRole[]) {
    const role = user.value?.role;
    // 闭集：Admin 不是通配符。需要管理员时把 "Admin" 写进名单。
    return !!role && roles.includes(role);
  }

  function is(...roles: UserRole[]) {
    return can(...roles);
  }

  return { token, user, isAuthed, login, logout, can, is };
});
