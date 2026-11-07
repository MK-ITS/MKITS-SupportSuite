// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

import { expect, test } from "@playwright/test";
import { signInAsAdmin } from "./support";

test("pages need a login, and every response carries the security headers", async ({ page }) => {
  const response = await page.goto("/sessions");
  await expect(page).toHaveURL(/\/account\/login/);
  const headers = response!.headers();
  expect(headers["content-security-policy"]).toContain("default-src");
  expect(headers["x-content-type-options"]).toBe("nosniff");
});

test("the portal follows the browser language", async ({ browser }) => {
  for (const [locale, lang] of [["de-DE", "de"], ["en-US", "en"]] as const) {
    const context = await browser.newContext({ locale });
    try {
      const page = await context.newPage();
      await page.goto("/portal/code");
      await expect(page.locator("html")).toHaveAttribute("lang", lang);
    } finally {
      await context.close();
    }
  }
});

test("the admin pages open", async ({ page }) => {
  await signInAsAdmin(page);
  for (const path of ["/admin/users", "/admin/devices", "/admin/ip-bans", "/admin/audit", "/admin/settings", "/account/manage"]) {
    const response = await page.goto(path);
    expect(response?.status(), path).toBe(200);
    await expect(page.locator("h1").first(), path).toBeVisible();
    await expect(page.getByRole("alert"), path).toHaveCount(0);
  }
});
