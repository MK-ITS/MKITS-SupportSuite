// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

import { describe, expect, it } from "vitest";
import { avcCodecString, type DecoderFactory, type DecoderLike, H264Stream, nalUnits } from "../src/video";

// Annex B: SPS (High profile 0x64, constraints 0x00, level 4.0 = 0x28), PPS, IDR slice.
const keyframe = new Uint8Array([0, 0, 0, 1, 0x67, 0x64, 0x00, 0x28, 0xac, 0, 0, 0, 1, 0x68, 0xee, 0x3c, 0x80, 0, 0, 1, 0x65, 0x88, 0x84]);
const delta = new Uint8Array([0, 0, 0, 1, 0x41, 0x9a, 0x02]);

class FakeDecoder implements DecoderLike {
  state = "unconfigured";
  readonly calls: string[] = [];
  configure(config: { codec: string }): void {
    this.state = "configured";
    this.calls.push(`configure ${config.codec}`);
  }
  decode(chunk: unknown): void {
    this.calls.push(`decode ${(chunk as { type: string }).type}`);
  }
  reset(): void {
    this.state = "unconfigured";
    this.calls.push("reset");
  }
  close(): void {
    this.state = "closed";
  }
}

function setup() {
  const decoder = new FakeDecoder();
  let requests = 0;
  let clock = 0;
  const factory: DecoderFactory = { create: () => decoder, chunk: (init) => init };
  const stream = new H264Stream(factory, () => undefined, () => requests++, () => clock);
  return { decoder, stream, requests: () => requests, advance: (ms: number) => (clock += ms) };
}

describe("H.264 helpers", () => {
  it("finds NAL units and builds the codec string from the SPS", () => {
    expect(nalUnits(keyframe)).toEqual([4, 13, 20]);
    expect(avcCodecString(keyframe)).toBe("avc1.640028");
    expect(avcCodecString(delta)).toBeNull();
  });
});

describe("H264Stream", () => {
  it("configures on the keyframe and decodes the following deltas", () => {
    const { decoder, stream, requests } = setup();
    expect(stream.decode(keyframe, true, 0, false)).toBe(true);
    expect(stream.decode(delta, false, 1, false)).toBe(true);
    expect(decoder.calls).toEqual(["configure avc1.640028", "decode key", "decode delta"]);
    expect(requests()).toBe(0);
  });

  it("drops deltas before the first keyframe and asks for one (at most once a second)", () => {
    const { decoder, stream, requests, advance } = setup();
    expect(stream.decode(delta, false, 0, false)).toBe(false);
    expect(stream.decode(delta, false, 1, false)).toBe(false);
    advance(1500);
    expect(stream.decode(delta, false, 2, false)).toBe(false);
    expect(decoder.calls).toEqual([]);
    expect(requests()).toBe(2);
  });

  it("after a gap drops deltas until the next keyframe", () => {
    const { decoder, stream, requests } = setup();
    stream.decode(keyframe, true, 0, false);
    expect(stream.decode(delta, false, 1, true)).toBe(false); // frames were lost before this one
    expect(stream.decode(delta, false, 2, false)).toBe(false);
    expect(stream.decode(keyframe, true, 3, false)).toBe(true);
    expect(stream.decode(delta, false, 4, false)).toBe(true);
    expect(decoder.calls).toEqual(["configure avc1.640028", "decode key", "reset", "configure avc1.640028", "decode key", "decode delta"]);
    expect(requests()).toBe(1);
  });

  it("a keyframe without SPS is treated as lost", () => {
    const { stream, requests } = setup();
    expect(stream.decode(delta, true, 0, false)).toBe(false);
    expect(requests()).toBe(1);
  });
});
