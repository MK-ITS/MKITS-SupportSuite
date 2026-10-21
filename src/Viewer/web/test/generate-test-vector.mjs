// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors
//
// Generates proto/testvectors/e2e-v0.json – a fixed E2E test vector checked by BOTH implementations
// (C#: Common.Tests/Crypto/TestVectorTests.cs, TypeScript: test/crypto.test.ts). Independent of the viewer code.
// Run: node test/generate-test-vector.mjs > ../../../proto/testvectors/e2e-v0.json
const { subtle } = globalThis.crypto;
const enc = new TextEncoder();
const hex = (b) => Buffer.from(b).toString("hex");
const concat = (...parts) => {
  const out = new Uint8Array(parts.reduce((s, p) => s + 4 + p.length, 0));
  const view = new DataView(out.buffer);
  let o = 0;
  for (const p of parts) { view.setUint32(o, p.length); out.set(p, o + 4); o += 4 + p.length; }
  return out;
};
const pair = async () => {
  const k = await subtle.generateKey({ name: "ECDH", namedCurve: "P-256" }, true, ["deriveBits"]);
  return { k, pkcs8: new Uint8Array(await subtle.exportKey("pkcs8", k.privateKey)), spki: new Uint8Array(await subtle.exportKey("spki", k.publicKey)) };
};
const sessionId = "0192f0c4-1c2d-7a3b-8000-00000000e2e0";
const agent = await pair();
const viewer = await pair();
const shared = await subtle.deriveBits({ name: "ECDH", public: viewer.k.publicKey }, agent.k.privateKey, 256);
const hk = await subtle.importKey("raw", shared, "HKDF", false, ["deriveBits"]);
const keys = new Uint8Array(await subtle.deriveBits({ name: "HKDF", hash: "SHA-256", salt: enc.encode(sessionId), info: concat(enc.encode("mkits/v0/e2e"), agent.spki, viewer.spki) }, hk, 512));
const aesKey = (raw) => subtle.importKey("raw", raw, "AES-GCM", false, ["encrypt"]);
const channel = 6, sequence = 1n;
const nonce = new Uint8Array(12); new DataView(nonce.buffer).setUint32(0, channel); new DataView(nonce.buffer).setBigUint64(4, sequence);
const sid = enc.encode(sessionId);
const aad = new Uint8Array(sid.length + 12); aad.set(sid); new DataView(aad.buffer).setUint32(sid.length, channel); new DataView(aad.buffer).setBigUint64(sid.length + 4, sequence);
const plaintext = enc.encode("Hello from the test vector");
const a2v = new Uint8Array(await subtle.encrypt({ name: "AES-GCM", iv: nonce, additionalData: aad }, await aesKey(keys.slice(0, 32)), plaintext));
const v2a = new Uint8Array(await subtle.encrypt({ name: "AES-GCM", iv: nonce, additionalData: aad }, await aesKey(keys.slice(32)), plaintext));
const digest = new Uint8Array(await subtle.digest("SHA-256", concat(enc.encode("mkits/v0/sas"), sid, agent.spki, viewer.spki)));
const code = (new DataView(digest.buffer).getUint32(0) % 1000000).toString().padStart(6, "0");
console.log(JSON.stringify({
  description: "E2E test vector, protocol v0 (docs/protocol.md §6). Both implementations must reproduce every value.",
  sessionId, channel, sequence: 1,
  agentPrivateKeyPkcs8: hex(agent.pkcs8), agentPublicKeySpki: hex(agent.spki),
  viewerPrivateKeyPkcs8: hex(viewer.pkcs8), viewerPublicKeySpki: hex(viewer.spki),
  plaintext: hex(plaintext),
  ciphertextAgentToViewer: hex(a2v), ciphertextViewerToAgent: hex(v2a),
  securityCode: `${code.slice(0, 3)} ${code.slice(3)}`,
}, null, 2));
