// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Core.Tests;

public sealed class InputAndClipboardTests
{
    private readonly RecordingContext _context = new();

    [Fact]
    public async Task Pointer_IsMappedToMonitorPixels_AndButtonTransitionsInjected()
    {
        var injector = new RecordingInjector();
        var input = new InputHandler(injector, new FakeScreenSource());

        await Send(input, new SessionPayload { PointerEvent = new PointerEvent { MonitorId = 2, X = 65535, Y = 0, Buttons = 1 } });
        await Send(input, new SessionPayload { PointerEvent = new PointerEvent { MonitorId = 2, X = 0, Y = 65535, Buttons = 1 } });
        await Send(input, new SessionPayload { PointerEvent = new PointerEvent { MonitorId = 2, X = 0, Y = 65535, Buttons = 0, WheelY = 50 } });

        Assert.Equal(["move 3199,0", "Left down", "move 1920,1023", "move 1920,1023", "Left up", "wheel 0,10"], injector.Calls);
    }

    [Fact]
    public async Task Pointer_UnknownMonitor_IsIgnored()
    {
        var injector = new RecordingInjector();
        var input = new InputHandler(injector, new FakeScreenSource());

        await Send(input, new SessionPayload { PointerEvent = new PointerEvent { MonitorId = 7, X = 1, Y = 1 } });

        Assert.Empty(injector.Calls);
    }

    [Fact]
    public async Task Keys_StillPressed_AreReleasedWhenViewerLeaves()
    {
        var injector = new RecordingInjector();
        var input = new InputHandler(injector, new FakeScreenSource());

        await Send(input, new SessionPayload { KeyEvent = new KeyEvent { Code = "ControlLeft", Down = true } });
        await Send(input, new SessionPayload { PointerEvent = new PointerEvent { MonitorId = 1, Buttons = 2 } });
        await Send(input, new SessionPayload { KeyEvent = new KeyEvent { Code = "KeyQ", Down = false } }); // release without press
        input.OnViewerGone("v1");

        Assert.Equal(["key ControlLeft down", "move 0,0", "Right down", "key ControlLeft up", "Right up"], injector.Calls);
    }

    [Fact]
    public async Task TextMode_And_SpecialKeys()
    {
        var injector = new RecordingInjector();
        var input = new InputHandler(injector, new FakeScreenSource());

        await Send(input, new SessionPayload { KeyEvent = new KeyEvent { Key = "€", Down = true, TextMode = true } });
        await Send(input, new SessionPayload { KeyEvent = new KeyEvent { Key = "€", Down = false, TextMode = true } });
        await Send(input, new SessionPayload { SpecialKey = new SpecialKey { Kind = SpecialKeyKind.CtrlAltDel } });

        Assert.Equal(["text €", "special CtrlAltDel"], injector.Calls);
    }

    [Fact]
    public async Task Clipboard_FromViewer_IsSet_AndNotEchoedBack()
    {
        var clipboard = new FakeClipboard { Text = "local secret" };
        var handler = new ClipboardHandler(clipboard);
        await handler.OnViewerReadyAsync(_context, "v1", CancellationToken.None);

        await handler.PollAsync(_context, CancellationToken.None);
        Assert.Empty(_context.Sent); // existing clipboard content is not pushed to a new supporter

        await handler.HandleAsync(_context, "v1", new SessionPayload { ClipboardText = new ClipboardText { Text = "from supporter" } }, CancellationToken.None);
        await handler.PollAsync(_context, CancellationToken.None);
        Assert.Equal("from supporter", clipboard.Text);
        Assert.Empty(_context.Sent);

        clipboard.Text = "copied by customer";
        await handler.PollAsync(_context, CancellationToken.None);
        var (viewerId, payload) = Assert.Single(_context.Sent);
        Assert.Null(viewerId);
        Assert.Equal("copied by customer", payload.ClipboardText.Text);
    }

    [Fact]
    public async Task Chat_ShowsMessages_AndTruncates()
    {
        var presenter = new RecordingPresenter();
        var chat = new ChatHandler(presenter);

        await chat.HandleAsync(_context, "v1", new SessionPayload { ChatMessage = new ChatMessage { Author = "Sam", Text = new string('x', 5000) } }, CancellationToken.None);
        await chat.SendAsync(_context, "Customer", "thanks", CancellationToken.None);

        var (author, text) = Assert.Single(presenter.Messages);
        Assert.Equal("Sam", author);
        Assert.Equal(ChatHandler.MaxTextLength, text.Length);
        Assert.Equal("thanks", Assert.Single(_context.Sent).Payload.ChatMessage.Text);
    }

    private Task Send(InputHandler input, SessionPayload payload) => input.HandleAsync(_context, "v1", payload, CancellationToken.None);

    private sealed class RecordingPresenter : IChatPresenter
    {
        public List<(string Author, string Text)> Messages { get; } = [];

        public void ShowMessage(string author, string text, DateTimeOffset timestamp) => Messages.Add((author, text));
    }
}
