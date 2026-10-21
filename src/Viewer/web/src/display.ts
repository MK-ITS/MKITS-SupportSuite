// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

import { create } from "@bufbuild/protobuf";
import {
  type Monitor,
  type SessionPayload,
  SessionPayloadSchema,
  type VideoFrame,
  VideoCodec,
} from "./gen/mkits/v0/session_pb";

/**
 * The area inside the canvas element that shows the image (CSS object-fit: contain letterboxes it).
 * Returns offsets and size relative to the element.
 */
export function contentBox(elementWidth: number, elementHeight: number, imageWidth: number, imageHeight: number): { left: number; top: number; width: number; height: number } {
  if (imageWidth <= 0 || imageHeight <= 0) {
    return { left: 0, top: 0, width: elementWidth, height: elementHeight };
  }
  const scale = Math.min(elementWidth / imageWidth, elementHeight / imageHeight);
  const width = imageWidth * scale;
  const height = imageHeight * scale;
  return { left: (elementWidth - width) / 2, top: (elementHeight - height) / 2, width, height };
}

/** Converts a position in the image area to protocol coordinates (0..65535 per monitor). */
export function normalize(offsetX: number, offsetY: number, displayWidth: number, displayHeight: number): { x: number; y: number } {
  const clamp = (v: number) => Math.max(0, Math.min(65535, Math.round(v)));
  return {
    x: clamp((offsetX / Math.max(1, displayWidth)) * 65535),
    y: clamp((offsetY / Math.max(1, displayHeight)) * 65535),
  };
}

/** W3C modifier bits as in KeyEvent.modifiers (bit 0 shift, 1 ctrl, 2 alt, 3 meta). */
export function modifierBits(e: { shiftKey: boolean; ctrlKey: boolean; altKey: boolean; metaKey: boolean }): number {
  return (e.shiftKey ? 1 : 0) | (e.ctrlKey ? 2 : 0) | (e.altKey ? 4 : 0) | (e.metaKey ? 8 : 0);
}

/** Mouse button mask from PointerEvent.buttons (the browser uses the same bit order as the protocol). */
export function buttonMask(browserButtons: number): number {
  // Browser: 1 left, 2 right, 4 middle, 8 back, 16 forward – identical to the protocol.
  return browserButtons & 0x1f;
}

/**
 * Draws received frames on a canvas (scaled to fit) and turns mouse/keyboard events on the canvas into input
 * payloads. Acknowledges every drawn frame (flow control) via `send`.
 */
export class RemoteDisplay {
  private monitorId = 0;
  private lastMove = 0;
  private pendingMove: SessionPayload | null = null;
  private moveScheduled = false;
  private drawing = Promise.resolve();
  private readonly context: CanvasRenderingContext2D;

  constructor(private readonly canvas: HTMLCanvasElement, private readonly send: (payload: SessionPayload) => void) {
    const context = canvas.getContext("2d");
    if (!context) {
      throw new Error("Canvas 2D not available");
    }
    this.context = context;
    canvas.tabIndex = 0;
    this.attachInput();
  }

  setMonitors(monitors: Monitor[]): void {
    const primary = monitors.find((m) => m.primary) ?? monitors[0];
    if (primary && this.monitorId === 0) {
      this.monitorId = primary.id;
    }
  }

  selectMonitor(id: number): void {
    this.monitorId = id;
    this.send(create(SessionPayloadSchema, { body: { case: "selectMonitor", value: { monitorId: id } } }));
  }

  /** Decodes and draws one frame, then acknowledges it. Frames are drawn strictly in order. */
  showFrame(frame: VideoFrame): void {
    this.drawing = this.drawing.then(async () => {
      if (frame.codec === VideoCodec.JPEG) {
        const bitmap = await createImageBitmap(new Blob([frame.data as BlobPart], { type: "image/jpeg" }));
        if (this.canvas.width !== bitmap.width || this.canvas.height !== bitmap.height) {
          this.canvas.width = bitmap.width;
          this.canvas.height = bitmap.height;
        }
        this.context.drawImage(bitmap, 0, 0);
        bitmap.close();
      }
      // H.264 (WebCodecs) follows with the H.264 encoder on the agent side.
      this.send(create(SessionPayloadSchema, { body: { case: "frameAck", value: { frameSequence: frame.frameSequence } } }));
    }).catch(() => undefined);
  }

  private attachInput(): void {
    const canvas = this.canvas;
    const pointer = (e: PointerEvent, wheelY = 0, wheelX = 0) => {
      const rect = canvas.getBoundingClientRect();
      const box = contentBox(rect.width, rect.height, canvas.width, canvas.height);
      const { x, y } = normalize(e.clientX - rect.left - box.left, e.clientY - rect.top - box.top, box.width, box.height);
      return create(SessionPayloadSchema, {
        body: { case: "pointerEvent", value: { monitorId: this.monitorId, x, y, buttons: buttonMask(e.buttons), wheelX, wheelY } },
      });
    };

    canvas.addEventListener("pointermove", (e) => {
      // At most ~60 moves per second; the last position always goes out.
      this.pendingMove = pointer(e);
      const now = performance.now();
      if (now - this.lastMove >= 16) {
        this.flushMove(now);
      } else if (!this.moveScheduled) {
        // One pending frame callback at a time; it sends the latest position.
        this.moveScheduled = true;
        requestAnimationFrame(() => {
          this.moveScheduled = false;
          this.flushMove(performance.now());
        });
      }
    });
    canvas.addEventListener("pointerdown", (e) => {
      canvas.focus();
      canvas.setPointerCapture(e.pointerId);
      this.send(pointer(e));
      e.preventDefault();
    });
    canvas.addEventListener("pointerup", (e) => {
      this.send(pointer(e));
      e.preventDefault();
    });
    canvas.addEventListener("contextmenu", (e) => e.preventDefault());
    canvas.addEventListener("wheel", (e) => {
      const notches = (delta: number) => (delta === 0 ? 0 : -Math.sign(delta) * Math.max(1, Math.round(Math.abs(delta) / 100)));
      this.send(pointer(e as unknown as PointerEvent, notches(e.deltaY), -notches(e.deltaX)));
      e.preventDefault();
    }, { passive: false });

    const key = (e: KeyboardEvent, down: boolean) => {
      if (!e.code) {
        return;
      }
      this.send(create(SessionPayloadSchema, {
        body: { case: "keyEvent", value: { code: e.code, key: e.key, down, modifiers: modifierBits(e), textMode: false } },
      }));
      e.preventDefault();
    };
    canvas.addEventListener("keydown", (e) => key(e, true));
    canvas.addEventListener("keyup", (e) => key(e, false));
  }

  private flushMove(now: number): void {
    if (this.pendingMove) {
      this.send(this.pendingMove);
      this.pendingMove = null;
      this.lastMove = now;
    }
  }
}
