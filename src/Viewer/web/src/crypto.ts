// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

/**
 * End-to-end crypto of the session plane (docs/protocol.md §6) with WebCrypto – byte-compatible with
 * src/Common/MKITS.SupportSuite.Common/Crypto/SessionCrypto.cs (verified by the shared test vector in
 * proto/testvectors/e2e-v0.json).
 */

const encoder = new TextEncoder();
const KDF_LABEL = encoder.encode("mkits/v0/e2e");
const SAS_LABEL = encoder.encode("mkits/v0/sas");
const TAG_BITS = 128;

const subtle = (): SubtleCrypto => globalThis.crypto.subtle;

/** Length-prefixed concatenation (4-byte big-endian length before each part). */
export function concat(...parts: Uint8Array[]): Uint8Array {
  const result = new Uint8Array(parts.reduce((sum, p) => sum + 4 + p.length, 0));
  const view = new DataView(result.buffer);
  let offset = 0;
  for (const part of parts) {
    view.setUint32(offset, part.length, false);
    result.set(part, offset + 4);
    offset += 4 + part.length;
  }
  return result;
}

export interface E2EKeyPair {
  readonly privateKey: CryptoKey;
  /** SubjectPublicKeyInfo (DER), sent in KeyExchange.ephemeral_public_key. */
  readonly publicKey: Uint8Array;
}

/** A fresh ephemeral ECDH P-256 key pair. The private key is not extractable. */
export async function generateKeyPair(): Promise<E2EKeyPair> {
  const pair = await subtle().generateKey({ name: "ECDH", namedCurve: "P-256" }, false, ["deriveBits"]);
  const spki = new Uint8Array(await subtle().exportKey("spki", pair.publicKey));
  return { privateKey: pair.privateKey, publicKey: spki };
}

/** For test vectors only: imports a PKCS#8 private key together with its SPKI public key. */
export async function importKeyPair(pkcs8: Uint8Array, spki: Uint8Array): Promise<E2EKeyPair> {
  const privateKey = await subtle().importKey("pkcs8", pkcs8 as BufferSource, { name: "ECDH", namedCurve: "P-256" }, false, ["deriveBits"]);
  return { privateKey, publicKey: spki };
}

/** The 6-digit security code, e.g. "042 917". */
export async function computeSecurityCode(sessionId: string, agentPublicKey: Uint8Array, viewerPublicKey: Uint8Array): Promise<string> {
  const digest = new Uint8Array(await subtle().digest("SHA-256", concat(SAS_LABEL, encoder.encode(sessionId), agentPublicKey, viewerPublicKey) as BufferSource));
  const value = new DataView(digest.buffer).getUint32(0, false) % 1_000_000;
  const digits = value.toString().padStart(6, "0");
  return `${digits.slice(0, 3)} ${digits.slice(3)}`;
}

/**
 * Viewer side of the key agreement: raw ECDH secret → HKDF-SHA256 (salt = session id,
 * info = label ‖ agent key ‖ viewer key) → key agent→viewer (receive) and viewer→agent (send).
 */
export async function establish(own: E2EKeyPair, sessionId: string, agentPublicKey: Uint8Array): Promise<SessionCipher> {
  const peer = await subtle().importKey("spki", agentPublicKey as BufferSource, { name: "ECDH", namedCurve: "P-256" }, false, []);
  const shared = await subtle().deriveBits({ name: "ECDH", public: peer }, own.privateKey, 256);
  const hkdfKey = await subtle().importKey("raw", shared, "HKDF", false, ["deriveBits"]);
  const keys = new Uint8Array(await subtle().deriveBits(
    { name: "HKDF", hash: "SHA-256", salt: encoder.encode(sessionId), info: concat(KDF_LABEL, agentPublicKey, own.publicKey) as BufferSource },
    hkdfKey,
    512));

  const agentToViewer = await subtle().importKey("raw", keys.slice(0, 32), "AES-GCM", false, ["decrypt"]);
  const viewerToAgent = await subtle().importKey("raw", keys.slice(32, 64), "AES-GCM", false, ["encrypt"]);
  keys.fill(0);
  return new SessionCipher(sessionId, viewerToAgent, agentToViewer);
}

/**
 * AES-256-GCM per direction; nonce = channel (u32 BE) ‖ sequence (u64 BE); AAD = session id ‖ channel ‖ sequence.
 * Sequence numbers start at 1 and must strictly increase per channel (replay protection).
 */
export class SessionCipher {
  private readonly sessionId: Uint8Array;
  private readonly lastSent = new Map<number, bigint>();
  private readonly lastReceived = new Map<number, bigint>();

  constructor(sessionId: string, private readonly sendKey: CryptoKey, private readonly receiveKey: CryptoKey) {
    this.sessionId = encoder.encode(sessionId);
  }

  /** Encrypts and returns the sequence number used. */
  async encrypt(channel: number, plaintext: Uint8Array): Promise<{ sequence: bigint; ciphertext: Uint8Array }> {
    const sequence = (this.lastSent.get(channel) ?? 0n) + 1n;
    this.lastSent.set(channel, sequence);
    const ciphertext = await subtle().encrypt(
      { name: "AES-GCM", iv: nonce(channel, sequence), additionalData: this.aad(channel, sequence), tagLength: TAG_BITS },
      this.sendKey,
      plaintext as BufferSource);
    return { sequence, ciphertext: new Uint8Array(ciphertext) };
  }

  /** Returns null for forged, replayed or out-of-order payloads. */
  async decrypt(channel: number, sequence: bigint, ciphertext: Uint8Array): Promise<Uint8Array | null> {
    if (sequence === 0n || ciphertext.length < TAG_BITS / 8) {
      return null;
    }
    const last = this.lastReceived.get(channel);
    if (last !== undefined && sequence <= last) {
      return null;
    }
    try {
      const plaintext = await subtle().decrypt(
        { name: "AES-GCM", iv: nonce(channel, sequence), additionalData: this.aad(channel, sequence), tagLength: TAG_BITS },
        this.receiveKey,
        ciphertext as BufferSource);
      // Re-check after the await: another decrypt for this channel may have completed meanwhile.
      const latest = this.lastReceived.get(channel);
      if (latest !== undefined && sequence <= latest) {
        return null;
      }
      this.lastReceived.set(channel, sequence);
      return new Uint8Array(plaintext);
    } catch {
      return null;
    }
  }

  private aad(channel: number, sequence: bigint): BufferSource {
    const data = new Uint8Array(this.sessionId.length + 12);
    data.set(this.sessionId, 0);
    const view = new DataView(data.buffer);
    view.setUint32(this.sessionId.length, channel, false);
    view.setBigUint64(this.sessionId.length + 4, sequence, false);
    return data;
  }
}

function nonce(channel: number, sequence: bigint): BufferSource {
  const data = new Uint8Array(12);
  const view = new DataView(data.buffer);
  view.setUint32(0, channel, false);
  view.setBigUint64(4, sequence, false);
  return data;
}
