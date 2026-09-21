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
    return role === "Admin" || (!!role && roles.includes(role));
  }

  return { token, user, isAuthed, login, logout, can };
});
