// SPDX-License-Identifier: AGPL-3.0-only
// @vitest-environment happy-dom
// Copyright (C) MK-ITSolutions and contributors

import { create } from "@bufbuild/protobuf";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { type SessionPayload, SessionPayloadSchema } from "../src/gen/mkits/v0/session_pb";

// xterm.js needs a real layout engine; the tests replace it with a small fake that records what is written.
const terminals: FakeTerminal[] = [];
class FakeTerminal {
  cols = 80;
  rows = 24;
  written: string[] = [];
  dataHandler: ((data: string) => void) | null = null;
  disposed = false;
  constructor() {
    terminals.push(this);
  }
  loadAddon(addon: { activate(t: FakeTerminal): void }): void {
    addon.activate(this);
  }
  open(): void {}
  focus(): void {}
  onData(handler: (data: string) => void): void {
    this.dataHandler = handler;
  }
  write(data: string | Uint8Array): void {
    this.written.push(typeof data === "string" ? data : new TextDecoder().decode(data));
  }
  dispose(): void {
    this.disposed = true;
  }
}
let nextSize: [number, number] | null = null;
vi.mock("@xterm/xterm", () => ({ Terminal: FakeTerminal }));
vi.mock("@xterm/addon-fit", () => ({
  FitAddon: class {
    terminal: FakeTerminal | null = null;
    activate(t: FakeTerminal) {
      this.terminal = t;
    }
    fit() {
      if (this.terminal && nextSize && !this.terminal.disposed && this.terminal === terminals.at(-1)) {
        [this.terminal.cols, this.terminal.rows] = nextSize;
      }
    }
  },
}));

const { RemoteTerminals } = await import("../src/terminal");

describe("RemoteTerminals", () => {
  let sent: SessionPayload[];
  let container: HTMLElement;

  beforeEach(() => {
    terminals.length = 0;
    nextSize = null;
    sent = [];
    container = document.createElement("section");
    container.hidden = true;
    document.body.append(container);
  });

  const create_ = () => new RemoteTerminals(container, (p) => sent.push(p), (key) => key);

  it("opens the default shell with the terminal size and forwards typed input", () => {
    const remote = create_();
    remote.open();

    expect(container.hidden).toBe(false);
    const open = sent[0];
    expect(open?.body.case === "shellOpen" && open.body.value).toMatchObject({ type: "", cols: 80, rows: 24 });

    terminals[0]!.dataHandler!("dir\r");
    const data = sent[1];
    expect(data?.body.case === "shellData" && new TextDecoder().decode(data.body.value.data)).toBe("dir\r");
  });

  it("writes output, shows the exit code once, and the refusal text for -1", () => {
    const remote = create_();
    remote.open();
    remote.open();
    const [first, second] = ["sh1", "sh2"];

    remote.handle(create(SessionPayloadSchema, { body: { case: "shellData", value: { shellId: first, data: new TextEncoder().encode("hello") } } }));
    remote.handle(create(SessionPayloadSchema, { body: { case: "shellExit", value: { shellId: first, exitCode: 3 } } }));
    remote.handle(create(SessionPayloadSchema, { body: { case: "shellExit", value: { shellId: first, exitCode: 3 } } }));
    remote.handle(create(SessionPayloadSchema, { body: { case: "shellExit", value: { shellId: second, exitCode: -1 } } }));

    expect(terminals[0]!.written[0]).toBe("hello");
    expect(terminals[0]!.written.filter((w) => w.includes("Viewer_TerminalExited 3"))).toHaveLength(1);
    expect(terminals[1]!.written.some((w) => w.includes("Viewer_TerminalRefused"))).toBe(true);
  });

  it("forwards a resize only when the size changed and the shell is running", () => {
    const remote = create_();
    remote.open();
    sent.length = 0;

    window.dispatchEvent(new Event("resize"));
    expect(sent).toHaveLength(0);

    nextSize = [100, 30];
    window.dispatchEvent(new Event("resize"));
    const resize = sent[0];
    expect(resize?.body.case === "shellResize" && resize.body.value).toMatchObject({ cols: 100, rows: 30 });

    remote.handle(create(SessionPayloadSchema, { body: { case: "shellExit", value: { shellId: "sh1", exitCode: 0 } } }));
    nextSize = [120, 40];
    window.dispatchEvent(new Event("resize"));
    expect(sent).toHaveLength(1);
  });

  it("closing sends ShellExit and hides the container when the last terminal is gone", () => {
    const remote = create_();
    remote.open();
    sent.length = 0;

    (container.querySelector("button") as HTMLButtonElement).click();

    expect(sent[0]?.body.case).toBe("shellExit");
    expect(terminals[0]!.disposed).toBe(true);
    expect(container.hidden).toBe(true);
  });
});
