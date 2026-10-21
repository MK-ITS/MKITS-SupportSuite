// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

import { describe, expect, it } from "vitest";
import { buttonMask, contentBox, modifierBits, normalize } from "../src/display";

describe("display helpers", () => {
  it("normalizes canvas coordinates to 0..65535 and clamps", () => {
    expect(normalize(0, 0, 800, 600)).toEqual({ x: 0, y: 0 });
    expect(normalize(800, 600, 800, 600)).toEqual({ x: 65535, y: 65535 });
    expect(normalize(400, 300, 800, 600)).toEqual({ x: 32768, y: 32768 });
    expect(normalize(-5, 9999, 800, 600)).toEqual({ x: 0, y: 65535 });
  });

  it("computes the letterboxed image area", () => {
    expect(contentBox(1000, 500, 1920, 1080)).toEqual({ left: (1000 - 888.888_888_888_888_9) / 2, top: 0, width: 888.888_888_888_888_9, height: 500 });
    expect(contentBox(800, 800, 1600, 800)).toEqual({ left: 0, top: 200, width: 800, height: 400 });
    expect(contentBox(800, 600, 0, 0)).toEqual({ left: 0, top: 0, width: 800, height: 600 });
  });

  it("maps modifiers and buttons", () => {
    expect(modifierBits({ shiftKey: true, ctrlKey: true, altKey: false, metaKey: true })).toBe(11);
    expect(buttonMask(1 | 2 | 64)).toBe(3);
  });
});
