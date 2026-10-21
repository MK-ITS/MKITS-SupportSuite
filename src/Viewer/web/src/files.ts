// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

import { create } from "@bufbuild/protobuf";
import { type FileChunk, type FileOffer, type SessionPayload, SessionPayloadSchema } from "./gen/mkits/v0/session_pb";
import { Sha256 } from "./sha256";

export const CHUNK_SIZE = 64 * 1024;
/** Received files are assembled in memory before download – keep that bounded. */
export const MAX_RECEIVE_SIZE = 2 * 1024 * 1024 * 1024;
const DECISION_TIMEOUT_MS = 2 * 60 * 1000;

/** What the page shows; implemented in main.ts. */
export interface FileUi {
  /** The customer offers a file; resolve true to accept. */
  confirm(name: string, size: number): Promise<boolean>;
  /** A received file is complete and verified. */
  received(name: string, data: Blob): void;
  progress(name: string, done: number, total: number): void;
  failed(name: string, reason: string): void;
}

interface Incoming {
  name: string;
  size: number;
  received: number;
  parts: Uint8Array[];
  hash: Sha256;
}

/** A file to upload (the browser File API, or anything with the same surface in tests). */
export interface UploadSource {
  readonly name: string;
  readonly size: number;
  slice(start: number, end: number): { arrayBuffer(): Promise<ArrayBuffer> };
}

/** Viewer side of file transfer (docs/protocol.md §5): upload to the customer, receive files offered by the customer. */
export class FileTransfers {
  private readonly incoming = new Map<string, Incoming>();
  private readonly decisions = new Map<string, (accepted: boolean) => void>();

  constructor(private readonly send: (payload: SessionPayload) => Promise<void>, private readonly ui: FileUi) {}

  async handle(payload: SessionPayload): Promise<void> {
    switch (payload.body.case) {
      case "fileOffer":
        await this.onOffer(payload.body.value);
        break;
      case "fileChunk":
        await this.onChunk(payload.body.value);
        break;
      case "fileComplete": {
        const transfer = this.incoming.get(payload.body.value.transferId);
        if (!transfer) {
          return;
        }
        this.incoming.delete(payload.body.value.transferId);
        const digest = transfer.hash.digest();
        if (transfer.received !== transfer.size || !equal(digest, payload.body.value.sha256)) {
          this.ui.failed(transfer.name, "checksum");
          return;
        }
        this.ui.received(transfer.name, new Blob(transfer.parts as BlobPart[], { type: "application/octet-stream" }));
        break;
      }
      case "fileDecision":
        this.decisions.get(payload.body.value.transferId)?.(payload.body.value.accepted);
        break;
      case "fileCancel": {
        const transfer = this.incoming.get(payload.body.value.transferId);
        if (transfer) {
          this.incoming.delete(payload.body.value.transferId);
          this.ui.failed(transfer.name, payload.body.value.reason || "cancelled");
        }
        this.decisions.get(payload.body.value.transferId)?.(false);
        break;
      }
      default:
        break;
    }
  }

  /** Offers a file to the customer; the customer must accept it. Returns false if declined or failed. */
  async upload(file: UploadSource): Promise<boolean> {
    const transferId = crypto.randomUUID().replaceAll("-", "");
    const decision = new Promise<boolean>((resolve) => {
      const timer = setTimeout(() => resolve(false), DECISION_TIMEOUT_MS);
      this.decisions.set(transferId, (accepted) => {
        clearTimeout(timer);
        resolve(accepted);
      });
    });

    await this.send(create(SessionPayloadSchema, { body: { case: "fileOffer", value: { transferId, name: file.name, size: BigInt(file.size) } } }));
    const accepted = await decision;
    this.decisions.delete(transferId);
    if (!accepted) {
      return false;
    }

    const hash = new Sha256();
    for (let offset = 0; offset < file.size; offset += CHUNK_SIZE) {
      const data = new Uint8Array(await file.slice(offset, Math.min(file.size, offset + CHUNK_SIZE)).arrayBuffer());
      hash.update(data);
      await this.send(create(SessionPayloadSchema, { body: { case: "fileChunk", value: { transferId, offset: BigInt(offset), data } } }));
      this.ui.progress(file.name, Math.min(file.size, offset + CHUNK_SIZE), file.size);
    }
    await this.send(create(SessionPayloadSchema, { body: { case: "fileComplete", value: { transferId, sha256: hash.digest() } } }));
    return true;
  }

  private async onOffer(offer: FileOffer): Promise<void> {
    const size = Number(offer.size);
    const name = safeName(offer.name);
    const acceptable = offer.transferId.length > 0 && offer.transferId.length <= 64 && size <= MAX_RECEIVE_SIZE && !this.incoming.has(offer.transferId);
    const accepted = acceptable && (await this.ui.confirm(name, size));
    if (accepted) {
      this.incoming.set(offer.transferId, { name, size, received: 0, parts: [], hash: new Sha256() });
    }
    await this.send(create(SessionPayloadSchema, { body: { case: "fileDecision", value: { transferId: offer.transferId, accepted } } }));
  }

  private async onChunk(chunk: FileChunk): Promise<void> {
    const transfer = this.incoming.get(chunk.transferId);
    if (!transfer) {
      return;
    }
    if (Number(chunk.offset) !== transfer.received || chunk.data.length > CHUNK_SIZE || transfer.received + chunk.data.length > transfer.size) {
      this.incoming.delete(chunk.transferId);
      this.ui.failed(transfer.name, "invalid_chunk");
      await this.send(create(SessionPayloadSchema, { body: { case: "fileCancel", value: { transferId: chunk.transferId, reason: "invalid_chunk" } } }));
      return;
    }
    transfer.parts.push(chunk.data);
    transfer.hash.update(chunk.data);
    transfer.received += chunk.data.length;
    this.ui.progress(transfer.name, transfer.received, transfer.size);
  }
}

/** File name for the browser download: no path parts, no control characters, never empty. */
export function safeName(name: string): string {
  const base = name.replaceAll("\\", "/").split("/").pop() ?? "";
  // eslint-disable-next-line no-control-regex
  const cleaned = base.replace(/[\u0000-\u001f<>:"|?*]/g, "_").trim().replace(/[. ]+$/, "");
  return cleaned.length > 0 ? cleaned.slice(0, 200) : "file";
}

function equal(a: Uint8Array, b: Uint8Array): boolean {
  if (a.length !== b.length) {
    return false;
  }
  let diff = 0;
  for (let i = 0; i < a.length; i++) {
    diff |= a[i]! ^ b[i]!;
  }
  return diff === 0;
}
