// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

import { describe, expect, it } from "vitest";
import { Sha256 } from "../src/sha256";

const webCrypto = async (data: Uint8Array) => new Uint8Array(await globalThis.crypto.subtle.digest("SHA-256", data as BufferSource));

describe("Sha256", () => {
  it.each([0, 1, 55, 56, 63, 64, 65, 1000, 200_000])("matches WebCrypto for %i bytes, fed in pieces", async (length) => {
    const data = new Uint8Array(length);
    for (let i = 0; i < length; i++) {
      data[i] = (i * 31 + 7) & 0xff;
    }
    const hash = new Sha256();
    for (let offset = 0; offset < length; offset += 777) {
      hash.update(data.subarray(offset, Math.min(length, offset + 777)));
    }
    expect(hash.digest()).toEqual(await webCrypto(data));
  });
});
