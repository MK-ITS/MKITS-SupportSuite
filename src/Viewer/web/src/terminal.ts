// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

import { create } from "@bufbuild/protobuf";
import { FitAddon } from "@xterm/addon-fit";
import { Terminal } from "@xterm/xterm";
import { type SessionPayload, SessionPayloadSchema } from "./gen/mkits/v0/session_pb";

interface OpenShell {
  terminal: Terminal;
  fit: FitAddon;
  panel: HTMLElement;
  exited: boolean;
}

/**
 * Remote shells in xterm.js (docs/protocol.md §5, channel SHELL). The agent decides which program runs; the viewer
 * only asks for the default shell. Output is written as bytes (UTF-8 with VT sequences), input is sent as typed.
 */
export class RemoteTerminals {
  private readonly shells = new Map<string, OpenShell>();
  private readonly encoder = new TextEncoder();
  private counter = 0;

  constructor(
    private readonly container: HTMLElement,
    private readonly send: (payload: SessionPayload) => void,
    private readonly t: (key: string) => string,
  ) {
    window.addEventListener("resize", () => {
      for (const [shellId, shell] of this.shells) {
        this.fit(shellId, shell);
      }
    });
  }

  /** Opens a terminal panel and asks the agent for its default shell. */
  open(): void {
    const shellId = `sh${++this.counter}`;
    const panel = document.createElement("div");
    panel.className = "viewer-terminal";

    const header = document.createElement("div");
    header.className = "viewer-terminal-header";
    const title = document.createElement("span");
    title.textContent = `${this.t("Viewer_Terminal")} ${this.counter}`;
    const close = document.createElement("button");
    close.type = "button";
    close.className = "secondary";
    close.textContent = this.t("Viewer_TerminalClose");
    close.addEventListener("click", () => this.close(shellId));
    header.append(title, close);

    const host = document.createElement("div");
    host.className = "viewer-terminal-host";
    panel.append(header, host);
    this.container.append(panel);
    this.container.hidden = false;

    const terminal = new Terminal({ cursorBlink: true, fontSize: 13, scrollback: 5000, convertEol: false });
    const fit = new FitAddon();
    terminal.loadAddon(fit);
    terminal.open(host);
    fit.fit();
    terminal.onData((data) => this.send(create(SessionPayloadSchema, { body: { case: "shellData", value: { shellId, data: this.encoder.encode(data) } } })));
    terminal.focus();

    const shell: OpenShell = { terminal, fit, panel, exited: false };
    this.shells.set(shellId, shell);
    this.send(create(SessionPayloadSchema, { body: { case: "shellOpen", value: { shellId, type: "", cols: terminal.cols, rows: terminal.rows, elevated: false } } }));
  }

  handle(payload: SessionPayload): void {
    switch (payload.body.case) {
      case "shellData":
        this.shells.get(payload.body.value.shellId)?.terminal.write(payload.body.value.data);
        break;
      case "shellExit": {
        const shell = this.shells.get(payload.body.value.shellId);
        if (shell && !shell.exited) {
          shell.exited = true;
          const code = payload.body.value.exitCode;
          shell.terminal.write(`\r\n\x1b[2m[${code === -1 ? this.t("Viewer_TerminalRefused") : `${this.t("Viewer_TerminalExited")} ${code}`}]\x1b[0m\r\n`);
        }
        break;
      }
      default:
        break;
    }
  }

  private close(shellId: string): void {
    const shell = this.shells.get(shellId);
    if (!shell) {
      return;
    }
    if (!shell.exited) {
      this.send(create(SessionPayloadSchema, { body: { case: "shellExit", value: { shellId, exitCode: 0 } } }));
    }
    shell.terminal.dispose();
    shell.panel.remove();
    this.shells.delete(shellId);
    this.container.hidden = this.shells.size === 0;
  }

  private fit(shellId: string, shell: OpenShell): void {
    const before = `${shell.terminal.cols}x${shell.terminal.rows}`;
    shell.fit.fit();
    if (!shell.exited && before !== `${shell.terminal.cols}x${shell.terminal.rows}`) {
      this.send(create(SessionPayloadSchema, { body: { case: "shellResize", value: { shellId, cols: shell.terminal.cols, rows: shell.terminal.rows } } }));
    }
  }
}
