// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

/**
 * H.264 in the browser viewer (ADR-0027): WebCodecs decoding of the agent's Annex B stream. Frames depend on each
 * other, so a gap in the frame sequence (the relay drops video for slow viewers) or a decoder error discards delta
 * frames until the next keyframe, which is requested from the agent.
 */

/** The parts of WebCodecs' VideoDecoder used here (injectable for tests). */
export interface DecoderLike {
  readonly state: string;
  configure(config: { codec: string; optimizeForLatency?: boolean }): void;
  decode(chunk: unknown): void;
  reset(): void;
  close(): void;
}

export interface DecoderFactory {
  create(output: (frame: { close(): void }) => void, error: (e: unknown) => void): DecoderLike;
  chunk(init: { type: "key" | "delta"; timestamp: number; data: Uint8Array }): unknown;
}

/** WebCodecs as available in the browser; `null` if there is no VideoDecoder. */
export function browserDecoderFactory(): DecoderFactory | null {
  if (typeof VideoDecoder === "undefined" || typeof EncodedVideoChunk === "undefined") {
    return null;
  }
  return {
    create: (output, error) => new VideoDecoder({ output: output as (frame: VideoFrame) => void, error: error as (e: DOMException) => void }),
    chunk: (init) => new EncodedVideoChunk(init),
  };
}

/** Whether this browser can decode the H.264 the agents send (checked once before announcing it). */
export async function supportsH264(): Promise<boolean> {
  if (typeof VideoDecoder === "undefined" || typeof EncodedVideoChunk === "undefined") {
    return false;
  }
  try {
    const result = await VideoDecoder.isConfigSupported({ codec: "avc1.42E01F", optimizeForLatency: true });
    return result.supported === true;
  } catch {
    return false;
  }
}

/** Start offsets of the NAL units of an Annex B stream (after the 00 00 01 / 00 00 00 01 start code). */
export function nalUnits(data: Uint8Array): number[] {
  const starts: number[] = [];
  for (let i = 0; i + 3 <= data.length; i++) {
    if (data[i] === 0 && data[i + 1] === 0 && data[i + 2] === 1) {
      starts.push(i + 3);
      i += 2;
    }
  }
  return starts;
}

/** `avc1.PPCCLL` from the sequence parameter set of a keyframe (profile, constraint flags, level); `null` without SPS. */
export function avcCodecString(data: Uint8Array): string | null {
  for (const start of nalUnits(data)) {
    if (((data[start] ?? 0) & 0x1f) === 7 && start + 3 < data.length) {
      const hex = (b: number | undefined) => (b ?? 0).toString(16).padStart(2, "0").toUpperCase();
      return `avc1.${hex(data[start + 1])}${hex(data[start + 2])}${hex(data[start + 3])}`;
    }
  }
  return null;
}

/** Decodes one viewer's H.264 stream; `draw` receives decoded frames (it must close them). */
export class H264Stream {
  private decoder: DecoderLike | null = null;
  private waitingForKeyframe = true;
  private lastRequest = -Infinity;

  constructor(
    private readonly factory: DecoderFactory,
    private readonly draw: (frame: { close(): void }) => void,
    private readonly requestKeyframe: () => void,
    private readonly now: () => number = () => performance.now(),
  ) {}

  /**
   * Feeds one frame. `gap` = frames were lost before it. Returns `true` if it was passed to the decoder; dropped
   * delta frames return `false` (the caller acknowledges every frame either way).
   */
  decode(data: Uint8Array, keyframe: boolean, timestampUs: number, gap: boolean): boolean {
    if (gap && !keyframe) {
      this.lose();
    }
    if (this.waitingForKeyframe && !keyframe) {
      this.askForKeyframe();
      return false;
    }

    if (keyframe) {
      const codec = avcCodecString(data);
      if (!codec) {
        this.lose();
        return false;
      }
      if (!this.decoder || this.decoder.state === "closed") {
        this.decoder = this.factory.create((frame) => this.draw(frame), () => this.lose());
      }
      // Every keyframe starts afresh (it carries SPS/PPS – the size may have changed with the monitor).
      this.decoder.configure({ codec, optimizeForLatency: true });
      this.waitingForKeyframe = false;
    }

    try {
      this.decoder!.decode(this.factory.chunk({ type: keyframe ? "key" : "delta", timestamp: timestampUs, data }));
      return true;
    } catch {
      this.lose();
      return false;
    }
  }

  close(): void {
    this.decoder?.close();
    this.decoder = null;
  }

  /** Something was lost or broke: drop deltas until a keyframe, and ask for one. */
  private lose(): void {
    this.waitingForKeyframe = true;
    if (this.decoder && this.decoder.state !== "closed") {
      try {
        this.decoder.reset();
      } catch {
        this.decoder = null;
      }
    }
    this.askForKeyframe();
  }

  /** At most one request per second (the agent sends one keyframe per request). */
  private askForKeyframe(): void {
    const now = this.now();
    if (now - this.lastRequest >= 1000) {
      this.lastRequest = now;
      this.requestKeyframe();
    }
  }
}
