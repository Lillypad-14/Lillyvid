using System;
using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace VideoSyncPrototype;

internal sealed class GameChatSender
{
    private const string ProcessChatBoxSignature = "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 48 8B F2 48 8B F9 45 84 C9";
    private const int MaxChatBytes = 500;

    private readonly ProcessChatBoxDelegate? processChatBox;

    private delegate void ProcessChatBoxDelegate(nint uiModule, nint message, nint unused, byte a4);

    public GameChatSender()
    {
        if (Plugin.SigScanner.TryScanText(ProcessChatBoxSignature, out var address))
        {
            this.processChatBox = Marshal.GetDelegateForFunctionPointer<ProcessChatBoxDelegate>(address);
        }
    }

    public bool IsReady => this.processChatBox is not null;

    public unsafe void ExecuteCommand(string command)
    {
        if (!command.StartsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Chat command must start with '/'.");
        }

        this.SendMessage(command);
    }

    private unsafe void SendMessage(string message)
    {
        if (this.processChatBox is null)
        {
            throw new InvalidOperationException("Could not find the native chat submit function.");
        }

        var bytes = Encoding.UTF8.GetBytes(message);
        if (bytes.Length == 0)
        {
            throw new ArgumentException("Chat message is empty.", nameof(message));
        }

        if (bytes.Length > MaxChatBytes)
        {
            throw new ArgumentException($"Chat message is {bytes.Length} bytes; the game chat limit is {MaxChatBytes} bytes.", nameof(message));
        }

        if (message.AnyInvalidChatCharacter())
        {
            throw new ArgumentException("Chat message contains a control character the game chat box cannot send.", nameof(message));
        }

        var uiModule = (nint)Framework.Instance()->GetUIModule();
        if (uiModule == 0)
        {
            throw new InvalidOperationException("The game UI module is not available yet.");
        }

        using var payload = new ChatPayload(bytes);
        var payloadPtr = Marshal.AllocHGlobal(400);
        try
        {
            Marshal.StructureToPtr(payload, payloadPtr, false);
            this.processChatBox(uiModule, payloadPtr, nint.Zero, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(payloadPtr);
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    private readonly struct ChatPayload : IDisposable
    {
        [FieldOffset(0)]
        private readonly nint textPtr;

        [FieldOffset(8)]
        private readonly ulong capacity;

        [FieldOffset(16)]
        private readonly ulong textLen;

        [FieldOffset(24)]
        private readonly ulong reserved;

        public ChatPayload(byte[] bytes)
        {
            this.textPtr = Marshal.AllocHGlobal(bytes.Length + 30);
            Marshal.Copy(bytes, 0, this.textPtr, bytes.Length);
            Marshal.WriteByte(this.textPtr + bytes.Length, 0);
            this.capacity = 64;
            this.textLen = (ulong)(bytes.Length + 1);
            this.reserved = 0;
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(this.textPtr);
        }
    }
}

internal static class ChatTextValidation
{
    public static bool AnyInvalidChatCharacter(this string value)
    {
        foreach (var ch in value)
        {
            if (char.IsControl(ch) && ch is not '\t')
            {
                return true;
            }
        }

        return false;
    }
}
