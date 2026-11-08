// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

import { expect, test } from "@playwright/test";
import { admin, saveState, submitFormOf, totp } from "./support";

test("first-run setup creates the admin, who must set up two-factor authentication", async ({ page }) => {
  const token = process.env.E2E_SETUP_TOKEN;
  test.skip(!token, "E2E_SETUP_TOKEN (from the server log) is required");

  await page.goto("/setup");
  await page.locator("#token").fill(token!);
  await page.locator("#username").fill(admin.userName);
  await page.locator("#email").fill(admin.email);
  await page.locator("#displayname").fill(admin.displayName);
  await page.locator("#password").fill(admin.password);
  await page.locator("#confirm").fill(admin.password);
  await submitFormOf(page, "token");
  await page.waitForURL(/\/account\/login\?setup=done/);

  // Setup is a one-time thing: afterwards the page does not exist.
  const again = await page.request.get("/setup");
  expect(again.status()).toBe(404);

  // Admins cannot work without two factors: the first login leads to the authenticator setup.
  await page.locator("#username").fill(admin.userName);
  await page.locator("#password").fill(admin.password);
  await submitFormOf(page, "password");
  await page.waitForURL(/\/account\/manage\/two-factor/);
  const key = (await page.locator("code.shared-key").innerText()).trim();
  expect(key).toMatch(/^[a-z2-7 ]+$/i);
  await page.locator("#code").fill(totp(key));
  await submitFormOf(page, "code");

  await expect(page.locator("ul.recovery-codes li")).toHaveCount(10);
  saveState({ totpKey: key });
});
