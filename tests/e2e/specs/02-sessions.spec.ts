// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

import { expect, test } from "@playwright/test";
import { signInAsAdmin, submitFormOf } from "./support";

test("a supporter creates a session, the customer enters its code, the viewer waits for the agent", async ({ page, browser }) => {
  await signInAsAdmin(page);

  // The page is interactive (Blazor Server): the form only works once its circuit is connected.
  const circuit = page.waitForEvent("websocket", (socket) => socket.url().includes("/_blazor"));
  await page.goto("/sessions");
  await circuit;
  const code = page.getByTestId("session-code");
  await expect(async () => {
    if (!(await code.isVisible())) {
      await page.locator("#session-label").fill("E2E customer");
      await submitFormOf(page, "session-label");
    }

    await expect(code).toBeVisible({ timeout: 3_000 });
  }).toPass({ timeout: 30_000 });
  const codeText = (await code.innerText()).trim();
  expect(codeText).toMatch(/^\d{4} \d{4}$/);

  // The customer (no cookies, another browser context) enters the code in the portal.
  const customer = await browser.newContext({ locale: "en-US" });
  try {
    const portal = await customer.newPage();
    await portal.goto("/portal/code");
    await portal.locator("#code").fill("0000 0000");
    await submitFormOf(portal, "code");
    await expect(portal.getByRole("alert")).toBeVisible(); // unknown code

    await portal.goto("/portal/code");
    await portal.locator("#code").fill(codeText);
    await submitFormOf(portal, "code");
    await expect(portal.getByRole("alert")).toHaveCount(0);
    await expect(portal.locator("h2").first()).toBeVisible(); // download step (packages may be missing in CI)
  } finally {
    await customer.close();
  }

  // The supporter opens the viewer: it connects to the relay and waits for the customer's client.
  await page.reload();
  const row = page.locator("tr", { hasText: "E2E customer" }).first();
  const href = await row.locator('a[href$="/view"]').getAttribute("href");
  expect(href).toMatch(/^\/sessions\/[0-9a-f-]+\/view$/);
  await page.goto(href!);
  await expect(page.locator("#viewer-canvas")).toBeVisible();
  await expect(page.locator("#viewer-status")).not.toBeEmpty();
});
