// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { computeSecurityCode, concat, establish, generateKeyPair, importKeyPair } from "../src/crypto";

const vector = JSON.parse(readFileSync(new URL("../../../../proto/testvectors/e2e-v0.json", import.meta.url), "utf8")) as Record<string, string | number>;
const bytes = (name: string): Uint8Array => Uint8Array.from((vector[name] as string).match(/../g) ?? [], (h) => parseInt(h, 16));

describe("crypto (shared test vector)", () => {
  it("reproduces the security code", async () => {
    const code = await computeSecurityCode(vector.sessionId as string, bytes("agentPublicKeySpki"), bytes("viewerPublicKeySpki"));
    expect(code).toBe(vector.securityCode);
  });

  it("decrypts agent → viewer and produces the same viewer → agent ciphertext", async () => {
    const viewer = await importKeyPair(bytes("viewerPrivateKeyPkcs8"), bytes("viewerPublicKeySpki"));
    const cipher = await establish(viewer, vector.sessionId as string, bytes("agentPublicKeySpki"));
    const channel = vector.channel as number;

    const plaintext = await cipher.decrypt(channel, 1n, bytes("ciphertextAgentToViewer"));
    expect(plaintext).toEqual(bytes("plaintext"));

    const { sequence, ciphertext } = await cipher.encrypt(channel, bytes("plaintext"));
    expect(sequence).toBe(1n);
    expect(ciphertext).toEqual(bytes("ciphertextViewerToAgent"));
  });
});

describe("crypto", () => {
  it("rejects replays, tampering and wrong channels", async () => {
    const viewer = await importKeyPair(bytes("viewerPrivateKeyPkcs8"), bytes("viewerPublicKeySpki"));
    const cipher = await establish(viewer, vector.sessionId as string, bytes("agentPublicKeySpki"));
    const ciphertext = bytes("ciphertextAgentToViewer");

    expect(await cipher.decrypt(5, 1n, ciphertext)).toBeNull();
    const tampered = ciphertext.slice();
    tampered[0] = (tampered[0] ?? 0) ^ 1;
    expect(await cipher.decrypt(6, 1n, tampered)).toBeNull();
    expect(await cipher.decrypt(6, 1n, ciphertext)).not.toBeNull();
    expect(await cipher.decrypt(6, 1n, ciphertext)).toBeNull();
    expect(await cipher.decrypt(6, 0n, ciphertext)).toBeNull();
  });

  it("generates P-256 keys as SPKI", async () => {
    const pair = await generateKeyPair();
    expect(pair.publicKey.length).toBe(91);
  });

  it("concat is length-prefixed", () => {
    expect(concat(new Uint8Array([7]))).toEqual(new Uint8Array([0, 0, 0, 1, 7]));
  });
});
