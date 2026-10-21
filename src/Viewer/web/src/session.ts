// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

import { create, fromBinary, type MessageInitShape, toBinary } from "@bufbuild/protobuf";
import {
  type Frame,
  FrameSchema,
  PeerRole,
  SessionStatus,
} from "./gen/mkits/v0/frame_pb";
import { Channel, type SessionPayload, SessionPayloadSchema } from "./gen/mkits/v0/session_pb";
import { computeSecurityCode, type E2EKeyPair, establish, generateKeyPair, type SessionCipher, verifyDeviceSignature } from "./crypto";

type FrameBody = NonNullable<MessageInitShape<typeof FrameSchema>["body"]>;

/** Protocol major/minor this viewer speaks (src/Common/.../ProtocolVersion.cs). */
export const PROTOCOL_MAJOR = 0;
export const PROTOCOL_MINOR = 3;
export const SUB_PROTOCOL = "mkits.v0";

/** What the UI needs to know. All callbacks are optional. */
export interface ViewerEvents {
  status?(status: SessionStatus, endReason?: string): void;
  securityCode?(code: string): void;
  payload?(payload: SessionPayload): void;
  error?(code: string): void;
  closed?(): void;
}

/** Minimal WebSocket surface (lets tests use a fake). */
export interface SocketLike {
  binaryType: string;
  readonly readyState: number;
  onopen: ((ev: unknown) => void) | null;
  onmessage: ((ev: { data: unknown }) => void) | null;
  onclose: ((ev: unknown) => void) | null;
  send(data: Uint8Array): void;
  close(): void;
}

/** Payload channel for a payload (docs/protocol.md §5) – mirrors Common/Protocol/PayloadChannels.cs. */
export function channelFor(payload: SessionPayload): Channel {
  switch (payload.body.case) {
    case "videoFrame":
      return Channel.VIDEO;
    case "pointerEvent":
    case "keyEvent":
    case "specialKey":
      return Channel.INPUT;
    case "monitorList":
    case "selectMonitor":
    case "qualitySettings":
    case "frameAck":
      return Channel.DISPLAY;
    case "clipboardText":
      return Channel.CLIPBOARD;
    case "fileOffer":
    case "fileDecision":
    case "fileChunk":
    case "fileComplete":
    case "fileCancel":
      return Channel.FILE;
    case "chatMessage":
      return Channel.CHAT;
    case "shellOpen":
    case "shellData":
    case "shellResize":
    case "shellExit":
      return Channel.SHELL;
    default:
      return Channel.UNSPECIFIED;
  }
}

/**
 * The supporter's side of a session: handshake with a one-time viewer ticket, waits for consent, starts the
 * E2E key exchange when the session becomes active, then encrypts/decrypts session-plane payloads.
 * The server never sees plaintext payloads.
 */
export class ViewerSession {
  private socket: SocketLike | null = null;
  private keyPair: E2EKeyPair | null = null;
  private cipher: SessionCipher | null = null;
  private sessionId = "";
  private deviceKey: Uint8Array | null = null;
  private sendQueue: Promise<void> = Promise.resolve();
  private receiveQueue: Promise<void> = Promise.resolve();
  private status: SessionStatus = SessionStatus.UNSPECIFIED;

  get currentStatus(): SessionStatus {
    return this.status;
  }

  constructor(private readonly events: ViewerEvents, private readonly openSocket: (url: string, protocol: string) => SocketLike) {}

  /** True once the key exchange finished and payloads can be sent. */
  get ready(): boolean {
    return this.cipher !== null;
  }

  /** `deviceKey` (SPKI): permanent device sessions – the agent's key exchange must carry a valid device signature. */
  connect(url: string, ticket: string, sessionId: string, deviceKey?: Uint8Array): void {
    this.sessionId = sessionId;
    this.deviceKey = deviceKey && deviceKey.length > 0 ? deviceKey : null;
    const socket = this.openSocket(url, SUB_PROTOCOL);
    socket.binaryType = "arraybuffer";
    this.socket = socket;

    socket.onopen = () => {
      this.sendFrame({ case: "hello", value: { protocolMajor: PROTOCOL_MAJOR, protocolMinor: PROTOCOL_MINOR, role: PeerRole.VIEWER, platform: "browser", productVersion: "web" } });
    };
    socket.onmessage = (event) => {
      if (!(event.data instanceof ArrayBuffer)) {
        return;
      }
      const data = new Uint8Array(event.data);
      // Process strictly in order (decryption is async, sequence numbers must be checked in order).
      this.receiveQueue = this.receiveQueue.then(() => this.onFrame(data, ticket)).catch(() => undefined);
    };
    socket.onclose = () => {
      this.cipher = null;
      this.events.closed?.();
    };
  }

  /** Encrypts and sends a payload to the agent. Ignored until the key exchange is complete. */
  send(payload: SessionPayload): Promise<void> {
    const cipher = this.cipher;
    if (!cipher) {
      return Promise.resolve();
    }
    const channel = channelFor(payload);
    const plaintext = toBinary(SessionPayloadSchema, payload);
    // Serialize: sequence numbers must reach the wire in the order they were assigned.
    this.sendQueue = this.sendQueue.then(async () => {
      const { sequence, ciphertext } = await cipher.encrypt(channel, plaintext);
      this.sendFrame({ case: "sessionData", value: { sessionId: this.sessionId, channel, sequence, ciphertext } });
    }).catch(() => undefined);
    return this.sendQueue;
  }

  /** Ends the session for everyone (supporter action). */
  end(): void {
    this.sendFrame({ case: "endSession", value: { sessionId: this.sessionId, reason: "ended_by_supporter" } });
  }

  close(): void {
    this.socket?.close();
  }

  private async onFrame(data: Uint8Array, ticket: string): Promise<void> {
    let frame: Frame;
    try {
      frame = fromBinary(FrameSchema, data);
    } catch {
      return;
    }

    const body = frame.body;
    switch (body.case) {
      case "helloAck":
        this.sendFrame({ case: "authenticate", value: { method: { case: "viewerTicket", value: { ticket } } } });
        break;
      case "authResult":
        if (!body.value.ok) {
          this.events.error?.("AUTH_FAILED");
        }
        break;
      case "sessionState":
        await this.onState(body.value.status, body.value.endReason);
        break;
      case "keyExchange":
        await this.onKeyExchange(body.value.ephemeralPublicKey, body.value.deviceSignature);
        break;
      case "sessionData": {
        const cipher = this.cipher;
        if (!cipher) {
          return;
        }
        const plaintext = await cipher.decrypt(body.value.channel, body.value.sequence, body.value.ciphertext);
        if (!plaintext) {
          return;
        }
        let payload: SessionPayload;
        try {
          payload = fromBinary(SessionPayloadSchema, plaintext);
        } catch {
          return;
        }
        if (channelFor(payload) === body.value.channel) {
          this.events.payload?.(payload);
        }
        break;
      }
      case "ping":
        this.sendFrame({ case: "pong", value: { timestampMs: body.value.timestampMs } });
        break;
      case "error":
        this.events.error?.(body.value.code);
        break;
      default:
        break;
    }
  }

  private async onState(status: SessionStatus, endReason: string): Promise<void> {
    this.status = status;
    this.events.status?.(status, endReason || undefined);

    if (status === SessionStatus.ACTIVE) {
      // Sent when consent was granted and again after the agent reconnected (it lost its keys):
      // start a fresh key exchange – old keys are never reused.
      this.cipher = null;
      this.keyPair = await generateKeyPair();
      this.sendFrame({ case: "keyExchange", value: { sessionId: this.sessionId, ephemeralPublicKey: this.keyPair.publicKey } });
    } else {
      this.cipher = null;
    }
  }

  private async onKeyExchange(agentPublicKey: Uint8Array, deviceSignature: Uint8Array): Promise<void> {
    const own = this.keyPair;
    if (!own) {
      return;
    }
    if (this.deviceKey && !(await verifyDeviceSignature(this.deviceKey, agentPublicKey, deviceSignature))) {
      // Not the enrolled device (or a man in the middle): never talk to it.
      this.cipher = null;
      this.events.error?.("DEVICE_SIGNATURE_INVALID");
      return;
    }
    try {
      this.cipher = await establish(own, this.sessionId, agentPublicKey);
    } catch {
      this.events.error?.("KEY_EXCHANGE_FAILED");
      return;
    }
    this.events.securityCode?.(await computeSecurityCode(this.sessionId, agentPublicKey, own.publicKey));
  }

  private sendFrame(body: FrameBody): void {
    if (this.socket && this.socket.readyState === 1) {
      this.socket.send(toBinary(FrameSchema, create(FrameSchema, { body })));
    }
  }
}

export { SessionStatus };
