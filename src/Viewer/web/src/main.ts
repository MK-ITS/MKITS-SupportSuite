// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

import { create } from "@bufbuild/protobuf";
import { SessionStatus } from "./gen/mkits/v0/frame_pb";
import { type SessionPayload, SessionPayloadSchema, SpecialKeyKind } from "./gen/mkits/v0/session_pb";
import { RemoteDisplay } from "./display";
import { type SocketLike, ViewerSession } from "./session";

/**
 * Entry point of the viewer page (/sessions/{id}/view). The server renders the ticket, the session id and all
 * localized texts into the page; no inline script is needed (strict CSP).
 */

type Texts = Record<string, string>;

function element<T extends HTMLElement>(id: string): T {
  const found = document.getElementById(id);
  if (!found) {
    throw new Error(`#${id} missing`);
  }
  return found as T;
}

function start(): void {
  const root = element<HTMLElement>("viewer");
  const texts = JSON.parse(element<HTMLScriptElement>("viewer-texts").textContent ?? "{}") as Texts;
  const t = (key: string, fallback = key): string => texts[key] ?? fallback;

  const status = element<HTMLElement>("viewer-status");
  const code = element<HTMLElement>("viewer-code");
  const monitors = element<HTMLSelectElement>("viewer-monitor");
  const chatLog = element<HTMLOListElement>("viewer-chat-log");
  const chatForm = element<HTMLFormElement>("viewer-chat-form");
  const chatInput = element<HTMLInputElement>("viewer-chat-input");
  const remoteClipboard = element<HTMLButtonElement>("viewer-clipboard-copy");
  let lastRemoteClipboard: string | null = null;

  const setStatus = (text: string) => {
    status.textContent = text;
  };

  const session = new ViewerSession(
    {
      status: (s, endReason) => {
        setStatus(t(`Viewer_Status_${SessionStatus[s]}`, SessionStatus[s]) + (endReason ? ` (${t(`Viewer_EndReason_${endReason}`, endReason)})` : ""));
        root.dataset.status = SessionStatus[s];
      },
      securityCode: (value) => {
        code.textContent = `${t("Viewer_SecurityCode")} ${value}`;
        code.hidden = false;
        // Start streaming with sensible defaults once the keys exist.
        void session.send(create(SessionPayloadSchema, { body: { case: "qualitySettings", value: { maxFps: 15, preferQuality: false } } }));
      },
      payload: (payload) => onPayload(payload),
      error: (errorCode) => setStatus(t(`Viewer_Error_${errorCode}`, errorCode)),
      closed: () => {
        if (root.dataset.status !== "ENDED") {
          setStatus(t("Viewer_Closed"));
        }
        root.dataset.status = "CLOSED";
      },
    },
    (url, protocol) => new WebSocket(url, protocol) as unknown as SocketLike,
  );

  const display = new RemoteDisplay(element<HTMLCanvasElement>("viewer-canvas"), (payload) => void session.send(payload));

  const onPayload = (payload: SessionPayload) => {
    switch (payload.body.case) {
      case "videoFrame":
        display.showFrame(payload.body.value);
        break;
      case "monitorList": {
        const list = payload.body.value.monitors;
        display.setMonitors(list);
        monitors.replaceChildren(...list.map((m) => {
          const option = document.createElement("option");
          option.value = String(m.id);
          option.textContent = `${t("Viewer_Monitor")} ${m.name}${m.primary ? ` (${t("Viewer_Primary")})` : ""} – ${m.width}×${m.height}`;
          option.selected = m.primary;
          return option;
        }));
        monitors.hidden = list.length < 2;
        break;
      }
      case "chatMessage":
        addChat(payload.body.value.author, payload.body.value.text);
        break;
      case "clipboardText":
        lastRemoteClipboard = payload.body.value.text;
        remoteClipboard.hidden = false;
        break;
      default:
        break;
    }
  };

  const addChat = (author: string, text: string) => {
    const item = document.createElement("li");
    const name = document.createElement("strong");
    name.textContent = `${author}: `;
    item.append(name, document.createTextNode(text)); // text nodes only – never innerHTML with remote content
    chatLog.append(item);
    item.scrollIntoView({ block: "end" });
  };

  monitors.addEventListener("change", () => display.selectMonitor(Number(monitors.value)));

  for (const button of root.querySelectorAll<HTMLButtonElement>("button[data-special]")) {
    button.addEventListener("click", () => {
      const kind = SpecialKeyKind[button.dataset.special as keyof typeof SpecialKeyKind];
      if (kind !== undefined) {
        void session.send(create(SessionPayloadSchema, { body: { case: "specialKey", value: { kind } } }));
      }
    });
  }

  element<HTMLButtonElement>("viewer-end").addEventListener("click", () => {
    root.dataset.status = "ENDED";
    session.end();
  });

  element<HTMLButtonElement>("viewer-clipboard-send").addEventListener("click", async () => {
    try {
      const text = await navigator.clipboard.readText();
      if (text) {
        await session.send(create(SessionPayloadSchema, { body: { case: "clipboardText", value: { text } } }));
      }
    } catch {
      setStatus(t("Viewer_ClipboardDenied"));
    }
  });

  remoteClipboard.addEventListener("click", async () => {
    if (lastRemoteClipboard !== null) {
      try {
        await navigator.clipboard.writeText(lastRemoteClipboard);
      } catch {
        setStatus(t("Viewer_ClipboardDenied"));
      }
    }
  });

  chatForm.addEventListener("submit", (e) => {
    e.preventDefault();
    const text = chatInput.value.trim();
    if (!text || !session.ready) {
      return;
    }
    chatInput.value = "";
    addChat(t("Viewer_ChatYou"), text);
    void session.send(create(SessionPayloadSchema, { body: { case: "chatMessage", value: { author: root.dataset.supporter ?? "", text, timestampMs: BigInt(Date.now()) } } }));
  });

  const wsUrl = new URL(root.dataset.ws ?? "/ws/viewer", window.location.href);
  wsUrl.protocol = wsUrl.protocol === "http:" ? "ws:" : "wss:";
  setStatus(t("Viewer_Connecting"));
  session.connect(wsUrl.toString(), root.dataset.ticket ?? "", root.dataset.sessionId ?? "");
}

if (document.readyState === "loading") {
  document.addEventListener("DOMContentLoaded", start);
} else {
  start();
}
