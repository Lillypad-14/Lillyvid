using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using VideoSyncPrototype.Windows;

namespace VideoSyncPrototype.Fun;

internal sealed class FunTab
{
    private const double SendCooldownSeconds = 1.5;
    private static readonly ChatDestination[] BaseDestinations =
    [
        new("Party", "/party"),
        new("Say", "/say"),
        new("Yell", "/yell"),
        new("Shout", "/shout"),
        new("Free Company", "/freecompany"),
        new("Alliance", "/alliance"),
    ];
    private static readonly string[] IntensityNames = ["Soft", "Normal", "Chaos"];

    private readonly Configuration config;
    private string source = string.Empty;
    private string preview = string.Empty;
    private string previewSource = string.Empty;
    private int previewIntensity = -1;
    private DateTime lastSentUtc = DateTime.MinValue;
    private string status = string.Empty;
    private bool statusIsError;

    public FunTab(Configuration config)
    {
        this.config = config;
        this.config.FunChatIntensity = Math.Clamp(this.config.FunChatIntensity, 0, IntensityNames.Length - 1);
        this.config.FunChatChannel = Math.Clamp(this.config.FunChatChannel, 0, GetDestinations().Count - 1);
    }

    public void Draw()
    {
        ImGui.Spacing();
        if (!UiTheme.BeginCollapsibleSection("UwU chat composer", defaultOpen: true, primary: true))
        {
            return;
        }

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 6f));
        ImGui.TextDisabled("Preview a translation, then choose exactly where to send it. Normal chat is never changed.");

        ImGui.TextUnformatted("Original message");
        if (ImGui.InputTextMultiline("##fun-source", ref this.source, 1024, new Vector2(-1f, 58f)))
        {
            this.status = string.Empty;
            if (this.config.FunChatAutoPreview)
            {
                this.UpdatePreview();
            }
        }

        this.DrawOptions();
        ImGui.Spacing();

        ImGui.TextUnformatted("Preview");
        var previewDisplay = this.preview;
        ImGui.InputTextMultiline(
            "##fun-preview",
            ref previewDisplay,
            2048,
            new Vector2(-1f, 58f),
            ImGuiInputTextFlags.ReadOnly);

        this.DrawMessageLength();
        ImGui.Spacing();
        this.DrawActions();

        if (!string.IsNullOrWhiteSpace(this.status))
        {
            ImGui.Spacing();
            UiTheme.StatusDot(this.statusIsError ? UiTheme.Danger : UiTheme.Live);
            ImGui.SameLine();
            ImGui.TextWrapped(this.status);
        }

        ImGui.PopStyleVar();
        ImGui.TreePop();
    }

    private void DrawOptions()
    {
        var destinations = GetDestinations();
        this.config.FunChatChannel = Math.Clamp(this.config.FunChatChannel, 0, destinations.Count - 1);
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var comboWidth = Math.Max(150f, (availableWidth - ImGui.GetStyle().ItemSpacing.X) * 0.5f);

        ImGui.SetNextItemWidth(comboWidth);
        if (ImGui.BeginCombo("##fun-channel", destinations[this.config.FunChatChannel].Label))
        {
            for (var i = 0; i < destinations.Count; i++)
            {
                var selected = i == this.config.FunChatChannel;
                if (ImGui.Selectable(destinations[i].Label, selected))
                {
                    this.config.FunChatChannel = i;
                    this.config.Save();
                    this.status = string.Empty;
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(comboWidth);
        if (ImGui.BeginCombo("##fun-intensity", IntensityNames[this.config.FunChatIntensity]))
        {
            for (var i = 0; i < IntensityNames.Length; i++)
            {
                var selected = i == this.config.FunChatIntensity;
                if (ImGui.Selectable(IntensityNames[i], selected))
                {
                    this.config.FunChatIntensity = i;
                    this.config.Save();
                    this.status = string.Empty;
                    if (this.config.FunChatAutoPreview)
                    {
                        this.UpdatePreview();
                    }
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.TextDisabled("Channel");
        ImGui.SameLine(comboWidth + ImGui.GetStyle().ItemSpacing.X);
        ImGui.TextDisabled("Intensity");

        var autoPreview = this.config.FunChatAutoPreview;
        if (ImGui.Checkbox("Auto-update preview", ref autoPreview))
        {
            this.config.FunChatAutoPreview = autoPreview;
            this.config.Save();
            if (autoPreview)
            {
                this.UpdatePreview();
            }
        }

        if (!this.config.FunChatAutoPreview)
        {
            ImGui.SameLine();
            if (UiTheme.QuietButton("Update preview"))
            {
                this.UpdatePreview();
            }
        }
    }

    private void DrawMessageLength()
    {
        if (string.IsNullOrEmpty(this.preview))
        {
            return;
        }

        var bytes = this.GetOutgoingByteCount(this.preview);
        var color = bytes > GameChat.MaxMessageBytes ? UiTheme.Danger : UiTheme.Muted;
        ImGui.TextColored(color, $"{bytes.ToString(CultureInfo.InvariantCulture)} / {GameChat.MaxMessageBytes} bytes");
    }

    private void DrawActions()
    {
        var cooldownRemaining = SendCooldownSeconds - (DateTime.UtcNow - this.lastSentUtc).TotalSeconds;
        var coolingDown = cooldownRemaining > 0;
        if (coolingDown)
        {
            ImGui.BeginDisabled();
        }

        if (UiTheme.PrimaryButton("Send", new Vector2(90f, 0f)))
        {
            this.Send();
        }

        if (coolingDown)
        {
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip($"Ready in {cooldownRemaining:0.0}s");
            }
        }

        ImGui.SameLine();
        if (UiTheme.QuietButton("Clear", new Vector2(75f, 0f)))
        {
            this.source = string.Empty;
            this.preview = string.Empty;
            this.previewSource = string.Empty;
            this.previewIntensity = -1;
            this.status = string.Empty;
        }
    }

    private void Send()
    {
        var original = this.source.Trim();
        if (original.Length == 0)
        {
            this.SetError("Enter a message before sending.");
            return;
        }

        if (original.StartsWith("/", StringComparison.Ordinal))
        {
            this.SetError("Commands cannot be sent from the Fun composer.");
            return;
        }

        if (original.IndexOfAny(['\r', '\n']) >= 0)
        {
            this.SetError("FFXIV chat messages must fit on one line. Remove the line break before sending.");
            return;
        }

        if (ContainsStructuredPayload(original))
        {
            this.SetError("Structured item, map, player, or auto-translate payloads are not supported here. Send those through normal chat so the game can preserve them.");
            return;
        }

        this.UpdatePreview();
        if (this.preview.Length == 0)
        {
            this.SetError("The translated message was empty.");
            return;
        }

        var destinations = GetDestinations();
        this.config.FunChatChannel = Math.Clamp(this.config.FunChatChannel, 0, destinations.Count - 1);
        var destination = destinations[this.config.FunChatChannel];
        if (!destination.Available)
        {
            this.SetError($"{destination.Label} is not available on this character.");
            return;
        }

        var outgoing = $"{destination.Command} {this.preview}";
        var byteCount = Encoding.UTF8.GetByteCount(outgoing);
        if (byteCount > GameChat.MaxMessageBytes)
        {
            this.SetError($"The translated message is {byteCount} bytes including its channel, over the game's {GameChat.MaxMessageBytes}-byte limit. Shorten the original message.");
            return;
        }

        if (!GameChat.TrySendMessage(outgoing, out var error))
        {
            this.SetError(error);
            return;
        }

        this.lastSentUtc = DateTime.UtcNow;
        this.statusIsError = false;
        this.status = $"Sent to {destination.Label}.";
    }

    private void UpdatePreview()
    {
        if (this.previewSource == this.source && this.previewIntensity == this.config.FunChatIntensity)
        {
            return;
        }

        this.preview = UwuTranslator.Translate(this.source, (UwuIntensity)this.config.FunChatIntensity);
        this.previewSource = this.source;
        this.previewIntensity = this.config.FunChatIntensity;
    }

    private int GetOutgoingByteCount(string message)
    {
        var destinations = GetDestinations();
        var index = Math.Clamp(this.config.FunChatChannel, 0, destinations.Count - 1);
        return Encoding.UTF8.GetByteCount($"{destinations[index].Command} {message}");
    }

    private void SetError(string message)
    {
        this.statusIsError = true;
        this.status = message;
    }

    private static bool ContainsStructuredPayload(string message)
    {
        foreach (var ch in message)
        {
            if ((char.IsControl(ch) && ch is not '\t') ||
                char.GetUnicodeCategory(ch) == UnicodeCategory.PrivateUse ||
                ch == '\uFFFC')
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<ChatDestination> GetDestinations()
    {
        var destinations = new List<ChatDestination>(BaseDestinations.Length + 16);
        destinations.AddRange(BaseDestinations);

        for (var i = 1; i <= 8; i++)
        {
            destinations.Add(new ChatDestination($"Linkshell {i}", $"/linkshell{i}"));
        }

        for (var i = 1; i <= GameChat.CrossworldLinkshellSlots; i++)
        {
            var name = GameChat.GetCrossworldLinkshellName(i);
            destinations.Add(new ChatDestination(
                name is null ? $"CWLS {i} (not available)" : $"CWLS {i}: {name}",
                $"/cwlinkshell{i}",
                name is not null));
        }

        return destinations;
    }

    private sealed record ChatDestination(string Label, string Command, bool Available = true);
}
