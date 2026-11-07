// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

import { createHmac } from "node:crypto";
import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { join } from "node:path";
import { expect, type Page } from "@playwright/test";

/** The admin created by the setup spec; the TOTP key is stored so later specs can sign in with two factors. */
export const admin = {
  userName: "e2e-admin",
  email: "e2e-admin@example.test",
  displayName: "E2E Admin",
  password: "e2e-correct-horse-battery",
};

// Outside test-results/ (Playwright empties that folder at the start of a run); git-ignored.
const stateFile = join(import.meta.dirname, "..", ".e2e-state.json");

export function saveState(state: { totpKey: string }): void {
  writeFileSync(stateFile, JSON.stringify(state));
}

export function loadState(): { totpKey: string } {
  if (!existsSync(stateFile)) {
    throw new Error("Run the setup spec first (it creates the admin).");
  }

  return JSON.parse(readFileSync(stateFile, "utf8")) as { totpKey: string };
}

/** RFC 6238 TOTP (SHA-1, 6 digits, 30 s) from the Base32 key the server shows. */
export function totp(base32Key: string, at: number = Date.now()): string {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
  const clean = base32Key.replace(/[\s=]/g, "").toUpperCase();
  let bits = "";
  for (const char of clean) {
    const value = alphabet.indexOf(char);
    if (value < 0) {
      throw new Error(`Invalid Base32 character ${char}`);
    }

    bits += value.toString(2).padStart(5, "0");
  }

  const key = Buffer.from(bits.match(/.{8}/g)!.map((b) => parseInt(b, 2)));
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(at / 1000 / 30)));
  const hmac = createHmac("sha1", key).update(counter).digest();
  const offset = hmac[hmac.length - 1]! & 0x0f;
  const code = (hmac.readUInt32BE(offset) & 0x7fffffff) % 1_000_000;
  return code.toString().padStart(6, "0");
}

/** Clicks the submit button of the form that contains the field with the given id (pages may have other forms). */
export async function submitFormOf(page: Page, fieldId: string): Promise<void> {
  await page.locator(`form:has(#${fieldId}) button[type=submit]`).first().click();
}

/** Signs the admin in, including the TOTP step. */
export async function signInAsAdmin(page: Page): Promise<void> {
  await page.goto("/account/login");
  await page.locator("#username").fill(admin.userName);
  await page.locator("#password").fill(admin.password);
  await submitFormOf(page, "password");
  await page.waitForURL(/\/account\/login-2fa/);
  await page.locator("#code").fill(totp(loadState().totpKey));
  await submitFormOf(page, "code");
  await expect(page).not.toHaveURL(/\/account\/login/);
}
