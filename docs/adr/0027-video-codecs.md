# ADR-0027: Video codecs – negotiation, per-viewer H.264 streams, JPEG fallback

- **Status:** Accepted
- **Date:** 2026-11-02

## Context

All agents send JPEG images so far: simple, robust against lost frames and decodable everywhere, but bandwidth-heavy
and blocky at low quality. ADR-0003 and the protocol plan H.264 (WebCodecs in the browser). H.264 frames depend on
each other: a viewer that misses one cannot decode the following ones until the next keyframe. Viewers of one session
can be throttled independently (flow control per viewer, relay drops video for slow peers), and on Windows capture
and encoding run in a helper process (ADR-0021/0022) – raw frames must not cross that pipe.

## Decision

- **Negotiation:** the viewer lists the codecs it can decode in `QualitySettings.codecs` (most preferred first;
  empty = JPEG only, so older viewers keep working). The agent picks the first codec its screen source can stream,
  else JPEG. Re-sending `QualitySettings` also asks for a keyframe.
- **Encoding stays with the capture:** `IScreenSource.CaptureStream(monitor, streamKey, codec, quality, keyframe)`
  returns encoded frames of a continuous stream; the source keeps one encoder per stream key (the viewer id) and
  releases it with `ReleaseStream`. Sources without a stream codec simply do not offer one (default members).
- **Per-viewer streams:** JPEG captures are still shared by viewers on the same monitor and quality; stream codecs get
  one encoder per viewer. A keyframe is forced for a new viewer, after a monitor or settings change and after a failed
  capture. Skipped captures (flow control) never break a stream: frames are only encoded when they are sent.
- **Lost frames:** the viewer watches `frame_sequence`; on a gap or a decoder error it re-sends `QualitySettings`
  and drops delta frames until the next keyframe.
- **Encoders:** H.264 Annex B. Windows: Media Foundation's H.264 encoder (licensed with the operating system), in the
  process that captures: the software encoder MFT (always present when Media Foundation is installed), constrained
  baseline, low-latency mode, BGRX converted to NV12 on the CPU; a keyframe is forced by starting a new encoder. A
  hardware encoder and GPU colour conversion can follow. The SYSTEM helper encodes in its own process and sends the
  access units over its pipe (8.2c); switching between own capture and helper releases the old side's encoder and
  starts the new side with a keyframe. Linux and Android follow separately (JPEG until then); the choice of a
  royalty-free or OS-provided encoder is made there.

## Consequences

- No change for existing viewers and agents (both default to JPEG).
- Several viewers on H.264 cost one encoder each; sessions usually have one or two viewers.
- The browser needs WebCodecs (all current Chromium browsers, Firefox ≥ 130, Safari ≥ 16.4) and keeps JPEG otherwise.
