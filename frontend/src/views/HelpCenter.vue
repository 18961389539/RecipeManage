<template>
  <main class="help-center" data-testid="help-center">
    <header class="help-heading">
      <div>
        <p class="help-kicker">{{ labels.kicker }}</p>
        <h1>{{ labels.title }}</h1>
        <p class="help-lede">{{ labels.lede }}</p>
      </div>
      <div class="help-count" aria-live="polite">
        <strong>{{ filteredArticles.length }}</strong>
        <span>{{ labels.guides }}</span>
      </div>
    </header>

    <section class="help-controls" :aria-label="labels.browse">
      <el-input
        ref="searchInput"
        v-model="query"
        class="help-search"
        :placeholder="labels.search"
        :aria-label="labels.search"
        clearable
        data-shortcut-search
      >
        <template #prefix><el-icon><Search /></el-icon></template>
        <template #suffix><kbd class="help-shortcut">/</kbd></template>
      </el-input>
      <nav class="help-categories" :aria-label="labels.categories">
        <button
          v-for="category in categoryOptions"
          :key="category.id"
          type="button"
          class="category-chip"
          :class="{ selected: activeCategory === category.id }"
          :aria-pressed="activeCategory === category.id"
          @click="selectCategory(category.id)"
        >
          {{ category.label }}
          <span>{{ category.count }}</span>
        </button>
      </nav>
    </section>

    <div class="help-layout">
      <aside class="help-index" :aria-label="labels.guideIndex">
        <div class="index-heading">
          <span>{{ labels.guideIndex }}</span>
          <span class="index-total">{{ filteredArticles.length }}</span>
        </div>
        <div v-if="filteredArticles.length" class="guide-list">
          <button
            v-for="article in filteredArticles"
            :key="article.id"
            type="button"
            class="guide-item"
            :class="{ active: selectedArticle?.id === article.id }"
            :aria-current="selectedArticle?.id === article.id ? 'page' : undefined"
            @click="openArticle(article.id)"
          >
            <span class="guide-item-top">
              <span class="guide-category">{{ categoryLabel(article.category) }}</span>
              <span class="guide-arrow" aria-hidden="true">↗</span>
            </span>
            <strong>{{ article.content[contentLocale].title }}</strong>
            <span class="guide-summary">{{ article.content[contentLocale].summary }}</span>
          </button>
        </div>
        <div v-else class="index-empty">
          <span class="empty-mark" aria-hidden="true">∅</span>
          <p>{{ labels.noResults }}</p>
          <button type="button" @click="resetFilters">{{ labels.reset }}</button>
        </div>
        <p class="index-footnote">{{ labels.roleNote }}</p>
      </aside>

      <article v-if="selectedArticle" class="help-article" aria-live="polite">
        <div class="article-topline">
          <span class="article-category">{{ categoryLabel(selectedArticle.category) }}</span>
          <span class="article-version">{{ labels.version }} {{ selectedArticle.contentVersion }}</span>
        </div>
        <h2>{{ articleCopy.title }}</h2>
        <p class="article-summary">{{ articleCopy.summary }}</p>

        <nav class="article-toc" :aria-label="labels.contents">
          <span class="toc-label">{{ labels.contents }}</span>
          <router-link
            v-for="section in articleCopy.sections"
            :key="section.id"
            :to="{ path: `/help/${selectedArticle.id}`, hash: `#${section.id}` }"
          >{{ section.title }}</router-link>
        </nav>

        <div class="article-body">
          <section
            v-for="(section, index) in articleCopy.sections"
            :id="section.id"
            :key="section.id"
            class="article-section"
          >
            <div class="section-heading">
              <span class="section-number">{{ String(index + 1).padStart(2, "0") }}</span>
              <h3>{{ section.title }}</h3>
            </div>
            <p v-if="section.intro" class="section-intro">{{ section.intro }}</p>
            <ol v-if="section.steps?.length" class="article-steps">
              <li v-for="step in section.steps" :key="step.title">
                <strong>{{ step.title }}</strong>
                <p>{{ step.text }}</p>
              </li>
            </ol>
            <ul v-if="section.bullets?.length" class="article-bullets">
              <li v-for="bullet in section.bullets" :key="bullet">{{ bullet }}</li>
            </ul>
            <aside v-if="section.note" class="article-note">
              <span aria-hidden="true">!</span>
              <p>{{ section.note }}</p>
            </aside>
          </section>
        </div>

        <footer class="article-footer">
          <div class="go-further" v-if="visibleLinks.length">
            <span class="toc-label">{{ labels.related }}</span>
            <router-link
              v-for="link in visibleLinks"
              :key="link.to"
              class="business-link"
              :to="link.to"
            >{{ link.label }} <span aria-hidden="true">→</span></router-link>
          </div>
          <div class="governance">
            <span>{{ labels.owner }} <strong>{{ selectedArticle.owner[contentLocale] }}</strong></span>
            <span>{{ labels.reviewed }} <strong>{{ selectedArticle.reviewedAt }}</strong></span>
          </div>
        </footer>
      </article>
      <section v-else class="help-article article-empty" aria-live="polite">
        <span class="empty-mark" aria-hidden="true">⌕</span>
        <h2>{{ labels.noResults }}</h2>
        <p>{{ labels.emptyHint }}</p>
      </section>
    </div>
  </main>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from "vue";
import { useRoute, useRouter } from "vue-router";
import { Search } from "@element-plus/icons-vue";
import { useI18n } from "vue-i18n";
import type { UserRole } from "../api/types";
import { HELP_ARTICLES, HELP_CATEGORIES, type HelpCategoryId, type HelpCopy, type HelpLink, type HelpLocale } from "../help/catalog";
import { useAuthStore } from "../stores/auth";

type CategoryFilter = "all" | HelpCategoryId;

const route = useRoute();
const router = useRouter();
const auth = useAuthStore();
const { locale } = useI18n({ useScope: "global" });
const searchInput = ref<{ input?: HTMLInputElement } | null>(null);
const query = ref("");
const activeCategory = ref<CategoryFilter>("all");
const isEnglish = computed(() => locale.value === "en");
const contentLocale = computed<HelpLocale>(() => isEnglish.value ? "en" : "zh-CN");
const role = computed(() => auth.user?.role);

const labels = computed(() => isEnglish.value ? {
  kicker: "BRMES / FIELD GUIDE",
  title: "Software guide",
  lede: "Practical workflows, checks, and operating rules for each part of the system.",
  guides: "guides",
  browse: "Browse guides",
  search: "Search topics, steps, or keywords",
  categories: "Guide categories",
  guideIndex: "GUIDE INDEX",
  noResults: "No guides match",
  reset: "Clear filters",
  roleNote: "Guides are filtered for your role. This does not grant access to any feature.",
  version: "EDITION",
  contents: "ON THIS PAGE",
  related: "CONTINUE IN BRMES",
  owner: "Owner",
  reviewed: "Reviewed",
  emptyHint: "Try another term or choose a different topic."
} : {
  kicker: "BRMES / 操作手册",
  title: "软件使用说明",
  lede: "按业务流程整理操作步骤、核对点与关键规则，帮助你安全完成日常工作。",
  guides: "篇指南",
  browse: "浏览指南",
  search: "搜索主题、步骤或关键词",
  categories: "指南分类",
  guideIndex: "指南目录",
  noResults: "没有匹配的指南",
  reset: "清除筛选",
  roleNote: "目录按当前角色筛选；阅读指南不会改变任何功能权限。",
  version: "内容版本",
  contents: "本篇目录",
  related: "前往相关功能",
  owner: "负责人",
  reviewed: "最近核对",
  emptyHint: "试试其他关键词，或切换到其他主题。"
});

const categoryLabels: Record<CategoryFilter, { zh: string; en: string }> = {
  all: { zh: "全部主题", en: "All topics" },
  start: { zh: "快速入门", en: "Getting started" },
  recipes: { zh: "配方与审核", en: "Recipes & review" },
  execution: { zh: "批次执行", en: "Batch execution" },
  quality: { zh: "质量与放行", en: "Quality & release" },
  materials: { zh: "设备与物料", en: "Equipment & materials" },
  system: { zh: "系统管理", en: "System administration" }
};

function articleApplies(articleRoleList: readonly UserRole[]) {
  return !role.value || articleRoleList.includes(role.value);
}

const availableArticles = computed(() => HELP_ARTICLES.filter(article => articleApplies(article.appliesTo)));
const filteredArticles = computed(() => {
  const term = query.value.trim().toLocaleLowerCase();
  return availableArticles.value.filter(article => {
    const copy = article.content[contentLocale.value];
    if (activeCategory.value !== "all" && article.category !== activeCategory.value) return false;
    if (!term) return true;
    const searchable = [
      copy.title,
      copy.summary,
      ...copy.keywords,
      ...copy.sections.flatMap(section => [
        section.title,
        section.intro ?? "",
        section.note ?? "",
        ...(section.bullets ?? []),
        ...(section.steps ?? []).flatMap(step => [step.title, step.text])
      ])
    ].join(" ").toLocaleLowerCase();
    return searchable.includes(term);
  });
});

const categoryOptions = computed(() => {
  const counts = new Map<CategoryFilter, number>();
  for (const article of availableArticles.value)
    counts.set(article.category, (counts.get(article.category) ?? 0) + 1);
  return HELP_CATEGORIES.map(category => ({
    id: category.id as CategoryFilter,
    label: categoryLabels[category.id as CategoryFilter][isEnglish.value ? "en" : "zh"],
    count: category.id === "all" ? availableArticles.value.length : counts.get(category.id as HelpCategoryId) ?? 0
  }));
});

const selectedArticle = computed(() => {
  const id = String(route.params.articleId ?? "");
  return filteredArticles.value.find(article => article.id === id) ?? null;
});
const articleCopy = computed<HelpCopy>(() =>
  selectedArticle.value?.content[contentLocale.value] ?? HELP_ARTICLES[0].content["zh-CN"]
);

function categoryLabel(id: HelpCategoryId) {
  return categoryLabels[id][isEnglish.value ? "en" : "zh"];
}

function canOpenLink(link: HelpLink) {
  const target = router.resolve(link.to);
  const requiredRoles = target.matched
    .map(record => record.meta.roles as UserRole[] | undefined)
    .find(candidate => candidate?.length);
  return !requiredRoles || auth.can(...requiredRoles);
}
const visibleLinks = computed(() => (articleCopy.value?.links ?? []).filter(canOpenLink));

function openArticle(id: string) {
  void router.push({ path: `/help/${id}` });
}
function selectCategory(id: CategoryFilter) {
  activeCategory.value = id;
}
function resetFilters() {
  query.value = "";
  activeCategory.value = "all";
}

async function scrollToHash() {
  await nextTick();
  const id = decodeURIComponent(route.hash.slice(1));
  if (!id) return;
  const behavior = window.matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth";
  document.getElementById(id)?.scrollIntoView({ behavior, block: "start" });
}

watch(
  () => [route.params.articleId, route.hash, role.value] as const,
  async ([rawId]) => {
    const id = String(rawId ?? "");
    if (!availableArticles.value.some(article => article.id === id)) {
      const fallback = availableArticles.value[0];
      if (fallback) {
        await router.replace({ path: `/help/${fallback.id}` });
        return;
      }
    }
    await scrollToHash();
  },
  { immediate: true }
);
watch(filteredArticles, articles => {
  const currentId = String(route.params.articleId ?? "");
  if (articles.length && !articles.some(article => article.id === currentId))
    openArticle(articles[0].id);
});

function onHelpShortcut(event: KeyboardEvent) {
  if (event.key !== "/" || event.ctrlKey || event.metaKey || event.altKey) return;
  const target = event.target;
  if (target instanceof HTMLElement && (
    target.isContentEditable || target.closest("input, textarea, select, [contenteditable='true']")
  )) return;
  event.preventDefault();
  searchInput.value?.input?.focus();
}
onMounted(() => window.addEventListener("keydown", onHelpShortcut));
onUnmounted(() => window.removeEventListener("keydown", onHelpShortcut));
</script>

<style scoped>
.help-center {
  max-width: 1480px;
  margin: 0 auto;
  padding: 0 2px var(--space-6);
  color: var(--text);
}
.help-heading {
  display: flex;
  align-items: flex-end;
  justify-content: space-between;
  gap: var(--space-4);
  padding: var(--space-2) 0 var(--space-4);
  border-bottom: 1px solid var(--line);
}
.help-kicker {
  margin: 0 0 5px;
  color: var(--accent-bright);
  font-size: 11px;
  font-weight: 700;
  letter-spacing: .12em;
}
.help-heading h1 {
  margin: 0;
  font-size: 21px;
  font-weight: 650;
  letter-spacing: -.02em;
}
.help-lede {
  margin: 6px 0 0;
  color: var(--muted);
  font-size: 13px;
}
.help-count {
  display: flex;
  align-items: baseline;
  gap: 7px;
  flex: none;
  padding: 8px 12px;
  border-left: 2px solid var(--accent);
  background: var(--sunken);
  color: var(--muted);
  font-size: 12px;
}
.help-count strong {
  color: var(--text);
  font-size: 20px;
  font-variant-numeric: tabular-nums;
}
.help-controls { padding: var(--space-3) 0 var(--space-4); }
.help-search { max-width: 540px; }
.help-search :deep(.el-input__wrapper) {
  min-height: 40px;
  background: var(--sunken);
  box-shadow: 0 0 0 1px var(--line) inset;
}
/* 上面这条与 EP 的 .el-input__wrapper.is-focus 特异性相同、且在其后声明，
   会把聚焦描边整个抹掉——搜索框点进去毫无反馈。补一条特异性更高的聚焦规则。 */
.help-search :deep(.el-input__wrapper.is-focus),
.help-search :deep(.el-input__wrapper:hover) {
  box-shadow: 0 0 0 1px var(--accent) inset;
}
.help-search :deep(.el-input__inner) { color: var(--text); }
.help-shortcut {
  display: inline-grid;
  place-items: center;
  min-width: 20px;
  height: 20px;
  border: 1px solid var(--line);
  border-radius: 4px;
  color: var(--muted);
  font: 11px ui-monospace, Consolas, monospace;
}
.help-categories {
  display: flex;
  gap: 6px;
  margin-top: var(--space-3);
  overflow-x: auto;
  padding: 2px 2px 4px;
  scrollbar-width: thin;
}
.category-chip {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  min-height: 30px;
  padding: 0 10px;
  border: 1px solid var(--line);
  border-radius: 5px;
  background: transparent;
  color: var(--text-body);
  font: inherit;
  font-size: 12px;
  white-space: nowrap;
  cursor: pointer;
}
.category-chip span {
  color: var(--muted);
  font-size: 11px;
  font-variant-numeric: tabular-nums;
}
.category-chip:hover { border-color: var(--accent); background: var(--hover); }
.category-chip.selected {
  border-color: var(--accent);
  background: var(--tint);
  color: var(--accent-bright);
}
.category-chip:focus-visible,
.guide-item:focus-visible,
.index-empty button:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}
.help-layout {
  display: grid;
  grid-template-columns: minmax(250px, 31%) minmax(0, 1fr);
  align-items: start;
  border: 1px solid var(--line);
  background: var(--panel);
}
.help-index {
  position: sticky;
  top: 0;
  max-height: calc(100vh - 156px);
  min-height: 420px;
  overflow: auto;
  border-right: 1px solid var(--line);
  background: var(--sunken);
}
.index-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 13px 14px 11px;
  border-bottom: 1px solid var(--line);
  color: var(--muted);
  font-size: 10px;
  font-weight: 700;
  letter-spacing: .1em;
  text-transform: uppercase;
}
.index-total {
  color: var(--accent-bright);
  font-variant-numeric: tabular-nums;
}
.guide-list { padding: 4px; }
.guide-item {
  display: block;
  width: 100%;
  padding: 11px 10px;
  border: 0;
  border-left: 2px solid transparent;
  border-radius: 3px;
  background: transparent;
  color: var(--text);
  text-align: left;
  cursor: pointer;
}
.guide-item + .guide-item { margin-top: 2px; }
.guide-item:hover { background: var(--hover); }
.guide-item.active {
  border-left-color: var(--accent);
  background: var(--tint);
}
.guide-item-top {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 5px;
}
.guide-category {
  color: var(--accent-bright);
  font-size: 10px;
  font-weight: 650;
  letter-spacing: .02em;
}
.guide-arrow { color: var(--muted); font-size: 12px; }
.guide-item strong {
  display: block;
  font-size: 13px;
  line-height: 1.45;
}
.guide-summary {
  display: -webkit-box;
  margin-top: 3px;
  overflow: hidden;
  color: var(--muted);
  font-size: 11px;
  line-height: 1.45;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
}
.index-footnote {
  margin: 8px 12px 12px;
  padding-top: 10px;
  border-top: 1px solid var(--line);
  color: var(--muted);
  font-size: 11px;
  line-height: 1.5;
}
.index-empty {
  display: grid;
  justify-items: start;
  gap: 8px;
  padding: 24px 14px;
  color: var(--muted);
  font-size: 12px;
}
.index-empty p { margin: 0; }
.index-empty button {
  padding: 0;
  border: 0;
  background: none;
  color: var(--accent-bright);
  font: inherit;
  cursor: pointer;
}
.empty-mark { color: var(--accent-bright); font-size: 22px; }
.help-article {
  min-width: 0;
  padding: clamp(18px, 3.2vw, 38px);
  background:
    linear-gradient(90deg, rgb(var(--accent-rgb) / .035) 1px, transparent 1px) 0 0 / 36px 36px,
    var(--panel);
}
.article-topline {
  display: flex;
  align-items: center;
  gap: 10px;
  color: var(--muted);
  font-size: 10px;
  font-weight: 650;
  letter-spacing: .08em;
  text-transform: uppercase;
}
.article-category { color: var(--accent-bright); }
.article-version {
  padding-left: 10px;
  border-left: 1px solid var(--line);
  font-variant-numeric: tabular-nums;
}
.help-article h2 {
  margin: 12px 0 6px;
  color: var(--text);
  font-size: clamp(21px, 2.4vw, 28px);
  line-height: 1.25;
  letter-spacing: -.025em;
}
.article-summary {
  max-width: 760px;
  margin: 0;
  color: var(--muted);
  font-size: 14px;
  line-height: 1.65;
}
.article-toc {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: 5px 14px;
  margin: 21px 0 4px;
  padding: 11px 0;
  border-top: 1px solid var(--line);
  border-bottom: 1px solid var(--line);
}
.toc-label {
  color: var(--muted);
  font-size: 10px;
  font-weight: 700;
  letter-spacing: .08em;
}
.article-toc a {
  color: var(--text-body);
  font-size: 12px;
  text-decoration: none;
}
.article-toc a:hover { color: var(--accent-bright); text-decoration: underline; }
.article-toc a:focus-visible,
.business-link:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 3px;
  border-radius: 2px;
}
.article-section {
  padding: 20px 0 17px;
  border-bottom: 1px solid var(--line);
  scroll-margin-top: 18px;
}
.section-heading {
  display: flex;
  align-items: baseline;
  gap: 10px;
}
.section-number {
  color: var(--accent-bright);
  font: 600 11px ui-monospace, Consolas, monospace;
  font-variant-numeric: tabular-nums;
}
.section-heading h3 {
  margin: 0;
  color: var(--text);
  font-size: 15px;
  font-weight: 650;
}
.section-intro {
  margin: 10px 0 0 30px;
  color: var(--text-body);
  font-size: 13px;
  line-height: 1.7;
}
.article-steps {
  display: grid;
  gap: 12px;
  margin: 13px 0 0 30px;
  padding: 0;
  list-style: none;
  counter-reset: help-step;
}
.article-steps li {
  position: relative;
  padding-left: 28px;
  counter-increment: help-step;
}
.article-steps li::before {
  position: absolute;
  top: 1px;
  left: 0;
  color: var(--muted);
  content: counter(help-step, decimal-leading-zero);
  font: 11px ui-monospace, Consolas, monospace;
}
.article-steps strong {
  color: var(--text-body);
  font-size: 13px;
}
.article-steps p {
  margin: 3px 0 0;
  color: var(--muted);
  font-size: 12px;
  line-height: 1.7;
}
.article-bullets {
  display: grid;
  gap: 8px;
  margin: 13px 0 0 30px;
  padding-left: 17px;
  color: var(--text-body);
  font-size: 13px;
  line-height: 1.65;
}
.article-note {
  display: grid;
  grid-template-columns: 20px minmax(0, 1fr);
  gap: 8px;
  margin: 13px 0 0 30px;
  padding: 10px 12px;
  border-left: 2px solid var(--warn);
  background: color-mix(in srgb, var(--warn) 8%, transparent);
}
.article-note > span {
  display: grid;
  place-items: center;
  width: 18px;
  height: 18px;
  border: 1px solid var(--warn);
  border-radius: 50%;
  color: var(--warn);
  font-size: 11px;
  font-weight: 700;
}
.article-note p {
  margin: 0;
  color: var(--text-body);
  font-size: 12px;
  line-height: 1.65;
}
.article-footer { padding-top: 15px; }
.go-further {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px 14px;
}
.business-link {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  color: var(--accent-bright);
  font-size: 12px;
  font-weight: 600;
  text-decoration: none;
}
.business-link:hover { text-decoration: underline; }
.governance {
  display: flex;
  flex-wrap: wrap;
  gap: 8px 20px;
  margin-top: 17px;
  padding-top: 11px;
  border-top: 1px solid var(--line);
  color: var(--muted);
  font-size: 11px;
}
.governance strong { margin-left: 4px; color: var(--text-body); font-weight: 500; }
.article-empty {
  display: grid;
  align-content: center;
  justify-items: start;
  min-height: 420px;
}
.article-empty .empty-mark { font-size: 28px; }
.article-empty h2 { font-size: 18px; }
.article-empty p { color: var(--muted); font-size: 13px; }

@media (max-width: 980px) {
  .help-layout { grid-template-columns: minmax(220px, 34%) minmax(0, 1fr); }
  .help-index { max-height: calc(100vh - 180px); }
  .help-article { padding: 22px; }
}
@media (max-width: 700px) {
  .help-heading { align-items: flex-start; }
  .help-heading h1 { font-size: 19px; }
  .help-lede { max-width: 38ch; font-size: 12px; }
  .help-count { padding: 6px 8px; }
  .help-count strong { font-size: 17px; }
  .help-layout { grid-template-columns: minmax(0, 1fr); }
  .help-index {
    position: static;
    max-height: none;
    min-height: 0;
    border-right: 0;
    border-bottom: 1px solid var(--line);
  }
  .guide-list {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(min(210px, 100%), 1fr));
    gap: 3px;
  }
  .guide-item + .guide-item { margin-top: 0; }
  .index-footnote { margin-top: 3px; }
  .help-article { padding: 18px 15px; }
  .section-intro, .article-steps, .article-bullets, .article-note { margin-left: 0; }
}
@media (prefers-reduced-motion: reduce) {
  *, *::before, *::after { scroll-behavior: auto !important; }
}
</style>
