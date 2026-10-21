// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

import { create } from "@bufbuild/protobuf";
import { describe, expect, it } from "vitest";
import { CHUNK_SIZE, FileTransfers, type FileUi, safeName } from "../src/files";
import { type SessionPayload, SessionPayloadSchema } from "../src/gen/mkits/v0/session_pb";

const sha = async (data: Uint8Array) => new Uint8Array(await crypto.subtle.digest("SHA-256", data as BufferSource));

function setup(accept = true) {
  const sent: SessionPayload[] = [];
  const events: string[] = [];
  let blob: Blob | null = null;
  const ui: FileUi = {
    confirm: async () => accept,
    received: (name, data) => {
      events.push(`received ${name}`);
      blob = data;
    },
    progress: () => undefined,
    failed: (name, reason) => events.push(`failed ${name} ${reason}`),
  };
  const transfers = new FileTransfers(async (p) => void sent.push(p), ui);
  return { transfers, sent, events, blob: () => blob };
}

const payload = (body: Parameters<typeof create<typeof SessionPayloadSchema>>[1]) => create(SessionPayloadSchema, body);

describe("FileTransfers", () => {
  it("receives a verified file offered by the customer", async () => {
    const { transfers, sent, events, blob } = setup();
    const content = new Uint8Array(CHUNK_SIZE + 100).map((_, i) => i & 0xff);

    await transfers.handle(payload({ body: { case: "fileOffer", value: { transferId: "t1", name: "../secret/report.pdf", size: BigInt(content.length) } } }));
    expect(sent[0]?.body.case === "fileDecision" && sent[0].body.value.accepted).toBe(true);

    await transfers.handle(payload({ body: { case: "fileChunk", value: { transferId: "t1", offset: 0n, data: content.slice(0, CHUNK_SIZE) } } }));
    await transfers.handle(payload({ body: { case: "fileChunk", value: { transferId: "t1", offset: BigInt(CHUNK_SIZE), data: content.slice(CHUNK_SIZE) } } }));
    await transfers.handle(payload({ body: { case: "fileComplete", value: { transferId: "t1", sha256: await sha(content) } } }));

    expect(events).toEqual(["received report.pdf"]);
    expect(new Uint8Array(await blob()!.arrayBuffer())).toEqual(content);
  });

  it("rejects wrong checksums and out-of-order chunks", async () => {
    const { transfers, sent, events } = setup();
    await transfers.handle(payload({ body: { case: "fileOffer", value: { transferId: "a", name: "a.bin", size: 4n } } }));
    await transfers.handle(payload({ body: { case: "fileChunk", value: { transferId: "a", offset: 0n, data: new Uint8Array(4) } } }));
    await transfers.handle(payload({ body: { case: "fileComplete", value: { transferId: "a", sha256: new Uint8Array(32) } } }));

    await transfers.handle(payload({ body: { case: "fileOffer", value: { transferId: "b", name: "b.bin", size: 10n } } }));
    await transfers.handle(payload({ body: { case: "fileChunk", value: { transferId: "b", offset: 5n, data: new Uint8Array(5) } } }));

    expect(events).toEqual(["failed a.bin checksum", "failed b.bin invalid_chunk"]);
    expect(sent.some((p) => p.body.case === "fileCancel")).toBe(true);
  });

  it("declines without storing when the supporter says no", async () => {
    const { transfers, sent } = setup(false);
    await transfers.handle(payload({ body: { case: "fileOffer", value: { transferId: "t", name: "x", size: 1n } } }));
    await transfers.handle(payload({ body: { case: "fileChunk", value: { transferId: "t", offset: 0n, data: new Uint8Array(1) } } }));

    expect(sent).toHaveLength(1);
    expect(sent[0]?.body.case === "fileDecision" && sent[0].body.value.accepted).toBe(false);
  });

  it("uploads in chunks after the customer accepted, with checksum", async () => {
    const { transfers, sent } = setup();
    const content = new Uint8Array(CHUNK_SIZE * 2 + 1).map((_, i) => (i * 7) & 0xff);
    const file = { name: "tool.zip", size: content.length, slice: (s: number, e: number) => ({ arrayBuffer: async () => content.slice(s, e).buffer }) };

    const upload = transfers.upload(file);
    await new Promise((r) => setTimeout(r, 0));
    const offer = sent[0];
    expect(offer?.body.case).toBe("fileOffer");
    const transferId = offer?.body.case === "fileOffer" ? offer.body.value.transferId : "";
    await transfers.handle(payload({ body: { case: "fileDecision", value: { transferId, accepted: true } } }));

    expect(await upload).toBe(true);
    const chunks = sent.filter((p) => p.body.case === "fileChunk");
    expect(chunks).toHaveLength(3);
    const complete = sent.at(-1);
    expect(complete?.body.case === "fileComplete" && complete.body.value.sha256).toEqual(await sha(content));
  });

  it("sanitizes names for downloads", () => {
    expect(safeName("C:\\Users\\x\\evil.exe")).toBe("evil.exe");
    expect(safeName("a<b>.txt")).toBe("a_b_.txt");
    expect(safeName("...")).toBe("file");
  });
});
