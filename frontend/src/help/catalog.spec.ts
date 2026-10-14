import { describe, expect, it } from "vitest";
import type { UserRole } from "../api/types";
import { HELP_ARTICLES, HELP_CATEGORIES, PAGE_GUIDE_ARTICLE_BY_KEY } from "./catalog";

const roles: UserRole[] = ["Admin", "ProcessEngineer", "Supervisor", "Quality", "Operator"];
const routePaths = new Set([
  "/dashboard", "/recipes", "/approvals", "/approval-chains", "/batches", "/alarms",
  "/equipment", "/audit", "/lots", "/users"
]);

describe("help catalog", () => {
  it("provides unique stable article and section IDs with complete bilingual copy", () => {
    const articleIds = HELP_ARTICLES.map(article => article.id);
    expect(new Set(articleIds).size).toBe(articleIds.length);

    for (const article of HELP_ARTICLES) {
      expect(article.id).toMatch(/^[a-z0-9-]+$/);
      expect(article.content["zh-CN"].title.trim()).not.toBe("");
      expect(article.content.en.title.trim()).not.toBe("");
      expect(article.content["zh-CN"].summary.trim()).not.toBe("");
      expect(article.content.en.summary.trim()).not.toBe("");
      expect(article.content["zh-CN"].sections.length).toBeGreaterThan(0);
      expect(article.content.en.sections.length).toBe(article.content["zh-CN"].sections.length);
      expect(article.contentVersion).toMatch(/^\d+\.\d+$/);
      expect(article.reviewedAt).toMatch(/^\d{4}-\d{2}-\d{2}$/);
      expect(article.owner["zh-CN"].trim()).not.toBe("");
      expect(article.owner.en.trim()).not.toBe("");
      expect(article.appliesTo.length).toBeGreaterThan(0);
      expect(article.appliesTo.every(role => roles.includes(role))).toBe(true);

      for (const lang of ["zh-CN", "en"] as const) {
        const ids = article.content[lang].sections.map(section => section.id);
        expect(new Set(ids).size).toBe(ids.length);
        expect(ids.every(id => /^[a-z0-9-]+$/.test(id))).toBe(true);
        expect(article.content[lang].links.every(link => routePaths.has(link.to))).toBe(true);
      }
    }
  });

  it("maps every page guide to an existing article and uses known categories", () => {
    const ids = new Set(HELP_ARTICLES.map(article => article.id));
    const categories = new Set(HELP_CATEGORIES.map(category => category.id));
    for (const article of HELP_ARTICLES) expect(categories.has(article.category)).toBe(true);
    for (const articleId of Object.values(PAGE_GUIDE_ARTICLE_BY_KEY)) expect(ids.has(articleId)).toBe(true);
  });
});
