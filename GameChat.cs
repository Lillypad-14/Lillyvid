using System;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace VideoSyncPrototype;

/// <summary>
/// Sends chat lines through the game's own chat box entry point (the same path
/// as typing them) and reads cross-world linkshell names for the picker.
/// </summary>
internal static unsafe class GameChat
{
    /// <summary>The game rejects chat lines longer than this many UTF-8 bytes.</summary>
    public const int MaxMessageBytes = 500;

    public const int CrossworldLinkshellSlots = 8;

    /// <summary>Must be called from the main/framework thread.</summary>
    public static bool TrySendMessage(string message, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(message))
        {
            error = "The chat message was empty.";
            return false;
        }

        if (message.IndexOfAny(['\r', '\n']) >= 0)
        {
            error = "Chat messages cannot contain line breaks.";
            return false;
        }

        if (Encoding.UTF8.GetByteCount(message) > MaxMessageBytes)
        {
            error = $"The message is longer than the game's {MaxMessageBytes}-byte chat limit.";
            return false;
        }

        var uiModule = UIModule.Instance();
        if (uiModule == null)
        {
            error = "The game UI is not ready yet.";
            return false;
        }

        var text = Utf8String.FromString(message);
        try
        {
            uiModule->ProcessChatBoxEntry(text, IntPtr.Zero, false);
        }
        finally
        {
            text->Dtor(true);
        }

        return true;
    }

    /// <summary>
    /// Sends a fully-encoded chat entry (SeString payload bytes, e.g. an item link),
    /// optionally with a leading channel command already prepended by the caller. Uses the
    /// game's own <see cref="Utf8String"/> so long messages are sized correctly.
    /// Must be called from the main/framework thread.
    /// </summary>
    public static bool TrySendEncoded(byte[] bytes, out string error)
    {
        error = string.Empty;
        if (bytes is not { Length: > 0 })
        {
            error = "The chat message was empty.";
            return false;
        }

        if (bytes.Length > MaxMessageBytes)
        {
            error = $"The message is longer than the game's {MaxMessageBytes}-byte chat limit.";
            return false;
        }

        var uiModule = UIModule.Instance();
        if (uiModule == null)
        {
            error = "The game UI is not ready yet.";
            return false;
        }

        // Utf8String.FromSequence reads up to a null terminator, so append one. SeString
        // payload bytes never contain an interior null.
        var buffer = new byte[bytes.Length + 1];
        Array.Copy(bytes, buffer, bytes.Length);

        fixed (byte* p = buffer)
        {
            var text = Utf8String.FromSequence(p);
            try
            {
                uiModule->ProcessChatBoxEntry(text, IntPtr.Zero, false);
            }
            finally
            {
                text->Dtor(true);
            }
        }

        return true;
    }

    /// <summary>
    /// Returns the name of the cross-world linkshell in the given slot (1-8),
    /// or null when the slot is empty or the info proxy is unavailable.
    /// </summary>
    public static string? GetCrossworldLinkshellName(int slot)
    {
        if (slot is < 1 or > CrossworldLinkshellSlots)
        {
            return null;
        }

        try
        {
            var proxy = InfoProxyCrossWorldLinkshell.Instance();
            if (proxy == null)
            {
                return null;
            }

            var name = proxy->GetCrossworldLinkshellName((uint)(slot - 1));
            if (name == null)
            {
                return null;
            }

            var value = name->ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }
}
