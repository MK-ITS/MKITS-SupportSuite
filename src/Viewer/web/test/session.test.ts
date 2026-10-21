// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

import { create, fromBinary, toBinary } from "@bufbuild/protobuf";
import { describe, expect, it } from "vitest";
import { concat } from "../src/crypto";
import { type Frame, FrameSchema, PeerRole, SessionStatus } from "../src/gen/mkits/v0/frame_pb";
import { Channel, SessionPayloadSchema } from "../src/gen/mkits/v0/session_pb";
import { channelFor, type SocketLike, ViewerSession } from "../src/session";

const { subtle } = globalThis.crypto;
const enc = new TextEncoder();
const SESSION_ID = "0192f0c4-1c2d-7a3b-8000-000000000042";

class FakeSocket implements SocketLike {
  binaryType = "";
  readyState = 0;
  onopen: ((ev: unknown) => void) | null = null;
  onmessage: ((ev: { data: unknown }) => void) | null = null;
  onclose: ((ev: unknown) => void) | null = null;
  readonly sent: Frame[] = [];
  send(data: Uint8Array): void {
    this.sent.push(fromBinary(FrameSchema, data));
  }
  close(): void {
    this.onclose?.({});
  }
  open(): void {
    this.readyState = 1;
    this.onopen?.({});
  }
  deliver(frame: Parameters<typeof create<typeof FrameSchema>>[1]): void {
    const bytes = toBinary(FrameSchema, create(FrameSchema, frame));
    this.onmessage?.({ data: bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength) });
  }
}

const flush = () => new Promise((r) => setTimeout(r, 20));

/** The agent side of the key agreement, implemented independently with WebCrypto. */
async function agentKeys(viewerSpki: Uint8Array) {
  const pair = await subtle.generateKey({ name: "ECDH", namedCurve: "P-256" }, true, ["deriveBits"]);
  const spki = new Uint8Array(await subtle.exportKey("spki", pair.publicKey));
  const viewerKey = await subtle.importKey("spki", viewerSpki as BufferSource, { name: "ECDH", namedCurve: "P-256" }, false, []);
  const shared = await subtle.deriveBits({ name: "ECDH", public: viewerKey }, pair.privateKey, 256);
  const hk = await subtle.importKey("raw", shared, "HKDF", false, ["deriveBits"]);
  const keys = new Uint8Array(await subtle.deriveBits({ name: "HKDF", hash: "SHA-256", salt: enc.encode(SESSION_ID), info: concat(enc.encode("mkits/v0/e2e"), spki, viewerSpki) as BufferSource }, hk, 512));
  return { spki, send: await subtle.importKey("raw", keys.slice(0, 32), "AES-GCM", false, ["encrypt"]), receive: await subtle.importKey("raw", keys.slice(32), "AES-GCM", false, ["decrypt"]) };
}

function nonceAndAad(channel: number, sequence: bigint) {
  const iv = new Uint8Array(12);
  new DataView(iv.buffer).setUint32(0, channel);
  new DataView(iv.buffer).setBigUint64(4, sequence);
  const sid = enc.encode(SESSION_ID);
  const aad = new Uint8Array(sid.length + 12);
  aad.set(sid);
  new DataView(aad.buffer).setUint32(sid.length, channel);
  new DataView(aad.buffer).setBigUint64(sid.length + 4, sequence);
  return { iv, additionalData: aad };
}

describe("ViewerSession", () => {
  it("handshake, key exchange on ACTIVE, encrypted payloads both ways", async () => {
    const socket = new FakeSocket();
    const received: string[] = [];
    let securityCode = "";
    const session = new ViewerSession(
      { payload: (p) => received.push(p.body.case === "chatMessage" ? p.body.value.text : String(p.body.case)), securityCode: (c) => (securityCode = c) },
      () => socket);

    session.connect("wss://example.test/ws/viewer", "ticket-123", SESSION_ID);
    socket.open();
    expect(socket.sent[0]?.body.case).toBe("hello");
    expect(socket.sent[0]?.body.case === "hello" && socket.sent[0].body.value.role).toBe(PeerRole.VIEWER);

    socket.deliver({ body: { case: "helloAck", value: { heartbeatSeconds: 30 } } });
    await flush();
    const auth = socket.sent[1];
    expect(auth?.body.case === "authenticate" && auth.body.value.method.case === "viewerTicket" && auth.body.value.method.value.ticket).toBe("ticket-123");

    socket.deliver({ body: { case: "sessionState", value: { sessionId: SESSION_ID, status: SessionStatus.ACTIVE } } });
    await flush();
    const exchange = socket.sent[2];
    expect(exchange?.body.case).toBe("keyExchange");
    const viewerSpki = exchange?.body.case === "keyExchange" ? exchange.body.value.ephemeralPublicKey : new Uint8Array();

    const agent = await agentKeys(viewerSpki);
    socket.deliver({ body: { case: "keyExchange", value: { sessionId: SESSION_ID, ephemeralPublicKey: agent.spki } } });
    await flush();
    expect(session.ready).toBe(true);
    expect(securityCode).toMatch(/^\d{3} \d{3}$/);

    // agent → viewer
    const chat = toBinary(SessionPayloadSchema, create(SessionPayloadSchema, { body: { case: "chatMessage", value: { text: "hi supporter" } } }));
    const ciphertext = new Uint8Array(await subtle.encrypt({ name: "AES-GCM", ...nonceAndAad(Channel.CHAT, 1n) }, agent.send, chat));
    socket.deliver({ body: { case: "sessionData", value: { sessionId: SESSION_ID, channel: Channel.CHAT, sequence: 1n, ciphertext } } });
    // replay is dropped
    socket.deliver({ body: { case: "sessionData", value: { sessionId: SESSION_ID, channel: Channel.CHAT, sequence: 1n, ciphertext } } });
    await flush();
    expect(received).toEqual(["hi supporter"]);

    // viewer → agent
    await session.send(create(SessionPayloadSchema, { body: { case: "chatMessage", value: { text: "hello customer" } } }));
    const data = socket.sent.at(-1);
    expect(data?.body.case).toBe("sessionData");
    if (data?.body.case === "sessionData") {
      const plaintext = await subtle.decrypt({ name: "AES-GCM", ...nonceAndAad(data.body.value.channel, data.body.value.sequence) }, agent.receive, data.body.value.ciphertext as BufferSource);
      const payload = fromBinary(SessionPayloadSchema, new Uint8Array(plaintext));
      expect(payload.body.case === "chatMessage" && payload.body.value.text).toBe("hello customer");
    }
  });

  it("answers pings and reports errors", async () => {
    const socket = new FakeSocket();
    const errors: string[] = [];
    const session = new ViewerSession({ error: (c) => errors.push(c) }, () => socket);
    session.connect("wss://example.test/ws/viewer", "t", SESSION_ID);
    socket.open();

    socket.deliver({ body: { case: "ping", value: { timestampMs: 5n } } });
    socket.deliver({ body: { case: "error", value: { code: "CONSENT_DENIED" } } });
    await flush();

    expect(socket.sent.some((f) => f.body.case === "pong")).toBe(true);
    expect(errors).toEqual(["CONSENT_DENIED"]);
  });

  it("does not send payloads before the key exchange", async () => {
    const socket = new FakeSocket();
    const session = new ViewerSession({}, () => socket);
    session.connect("wss://example.test/ws/viewer", "t", SESSION_ID);
    socket.open();

    await session.send(create(SessionPayloadSchema, { body: { case: "chatMessage", value: { text: "too early" } } }));

    expect(socket.sent.filter((f) => f.body.case === "sessionData")).toHaveLength(0);
  });

  it("maps payloads to channels like the C# implementation", () => {
    expect(channelFor(create(SessionPayloadSchema, { body: { case: "pointerEvent", value: {} } }))).toBe(Channel.INPUT);
    expect(channelFor(create(SessionPayloadSchema, { body: { case: "frameAck", value: {} } }))).toBe(Channel.DISPLAY);
    expect(channelFor(create(SessionPayloadSchema, {}))).toBe(Channel.UNSPECIFIED);
  });
});
