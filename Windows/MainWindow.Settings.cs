using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using VideoSyncPrototype.Emotes;
using VideoSyncPrototype.Fun;
using VideoSyncPrototype.Rendering;

namespace VideoSyncPrototype.Windows;

public sealed partial class MainWindow
{
    // Settings tab + diagnostics / renderer probe.

    private void DrawSettingsTab(bool running)
    {
        ImGui.Spacing();

        // Primary setting: the one thing most people ever touch here.
        UiTheme.SectionTitle("Watch2Gether account");
        ImGui.TextWrapped("Your free key lets you create rooms. Get one from w2g.tv → Edit Profile → API Access.");
        this.DrawApiKeyInput();
        if (ImGui.SmallButton("Get a key"))
        {
            this.OpenUrl("https://w2g.tv/");
        }

        ImGui.Spacing();
        if (UiTheme.BeginCollapsibleSection("Watch2Gether room cleanup"))
        {
            ImGui.TextWrapped("Watch2Gether does not expose a bulk delete API for temporary rooms. Use the dashboard to delete old rooms from your account; the plugin can also forget the current share code here.");
            ImGui.Spacing();

            if (UiTheme.PrimaryButton("Open room dashboard"))
            {
                this.OpenUrl("https://w2g.tv/en/account/dashboard/");
                this.status = "Opened the Watch2Gether room dashboard.";
            }

            ImGui.SameLine();
            var hasRoom = !string.IsNullOrWhiteSpace(this.lastWatch2GetherRoomUrl) ||
                          !string.IsNullOrWhiteSpace(this.lastWatch2GetherRoomCode);
            if (!hasRoom)
            {
                ImGui.BeginDisabled();
            }

            if (ImGui.Button("Forget current room"))
            {
                this.lastWatch2GetherRoomUrl = string.Empty;
                this.lastWatch2GetherRoomCode = string.Empty;
                this.lastOutboundWatch2GetherRoomKey = string.Empty;
                this.lastOutboundWatch2GetherRoomUtc = DateTime.MinValue;
                this.status = "Cleared the current Watch2Gether room from the plugin.";
            }

            if (!hasRoom)
            {
                ImGui.EndDisabled();
            }

            ImGui.TreePop();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (UiTheme.BeginCollapsibleSection("Nearby Sync relay"))
        {
            ImGui.TextWrapped("Nearby Sync works out of the box through Lillypad's public relay. Change this only if you host your own relay.");
            ImGui.Spacing();
            this.DrawNetworkSyncRelaySettings();
            ImGui.TreePop();
        }

        // Everything below is niche, so it stays collapsed by default and out of the way.
        if (UiTheme.BeginCollapsibleSection("Legacy party sync (chat channel)"))
        {
            ImGui.Spacing();
            this.DrawLegacyPartySync(running);
            ImGui.Spacing();
            ImGui.TreePop();
        }

        if (UiTheme.BeginCollapsibleSection("Diagnostics & troubleshooting"))
        {
            ImGui.Spacing();
            this.DrawDiagnostics();
            ImGui.TreePop();
        }
    }

    private void DrawNetworkSyncRelaySettings()
    {
        var relayUrl = this.GetNetworkSyncRelayUrl();
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputTextWithHint("##nearby-sync-relay", Configuration.DefaultNetworkSyncRelayUrl, ref relayUrl, 256))
        {
            this.config.NetworkSyncRelayUrl = relayUrl.Trim();
            this.config.Save();
        }

        ImGui.TextDisabled("Default: " + Configuration.DefaultNetworkSyncRelayUrl);
        if (ImGui.Button("Reset to default"))
        {
            this.config.NetworkSyncRelayUrl = Configuration.DefaultNetworkSyncRelayUrl;
            this.config.Save();
            this.status = "Nearby Sync relay reset to the Lillypad default.";
        }

        if (this.networkSync.State != NetworkSync.SyncState.Disabled)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("Disable and re-enable Nearby Sync to use a changed relay.");
        }
    }

    private void DrawDiagnostics()
    {
        ImGui.TextDisabled(this.BuildTechStatusLine());
        ImGui.Spacing();

        if (ImGui.Button("Pop-out player window"))
        {
            this.SurfaceWindow.IsOpen = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("Test fresh receive"))
        {
            this.TestReceiveSnowSync();
        }

        ImGui.SameLine();
        if (ImGui.Button("Open pasted code in overlay"))
        {
            if (TryDecode(this.pasteCode, out var payload, out var error))
            {
                this.OpenInOverlay(payload);
            }
            else
            {
                this.status = error;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Copy native status"))
        {
            ImGui.SetClipboardText(this.presentHookProbe.Status);
            this.status = "Copied the native renderer status to the clipboard.";
        }

        ImGui.Spacing();
        ImGui.Separator();
        UiTheme.SectionTitle("Renderer diagnostics");
        this.DrawRealRendererProbe();

        ImGui.Separator();
        UiTheme.SectionTitle("Fallback preview");
        this.DrawInWindowPreview();
    }

    private void DrawRealRendererProbe()
    {
        ImGui.TextWrapped("The 3D screen installs native D3D hooks automatically when you place it. These controls exist only for troubleshooting.");

        if (ImGui.Button("Probe Real Renderer"))
        {
            this.realRendererProbe = RenderProbe.Capture();
            this.status = "Captured real renderer probe data. If the key pointers are non-null, the next step is a D3D draw hook.";
        }

        ImGui.SameLine();
        if (ImGui.Button("Install Native Hooks"))
        {
            this.status = this.presentHookProbe.TryInstall()
                ? "Installed native D3D hooks. If frame and target-bind counts climb, we can start drawing against real scene depth."
                : "Could not install the native D3D hooks yet. Check the renderer probe output.";
            Plugin.ChatGui.Print($"[VideoSync] {this.status}");
        }

        ImGui.SameLine();
        if (ImGui.Button("Disable Native Hooks"))
        {
            this.presentHookProbe.Disable();
            this.status = "Disabled the native D3D hooks.";
            Plugin.ChatGui.Print($"[VideoSync] {this.status}");
        }

        var nativeTestDraw = this.presentHookProbe.NativeTestDrawEnabled;
        if (ImGui.Checkbox("Native test screen quad", ref nativeTestDraw))
        {
            this.presentHookProbe.NativeTestDrawEnabled = nativeTestDraw;
            this.status = nativeTestDraw
                ? "Enabled the native D3D screen quad. It should follow the world screen placement as a translucent green rectangle."
                : "Disabled the native D3D screen quad.";
            Plugin.ChatGui.Print($"[VideoSync] {this.status}");
        }

        var nativeScreenProbe = this.presentHookProbe.NativeScreenSpaceProbeEnabled;
        if (ImGui.Checkbox("Native screen-space probe", ref nativeScreenProbe))
        {
            this.presentHookProbe.NativeScreenSpaceProbeEnabled = nativeScreenProbe;
            this.status = nativeScreenProbe
                ? "Enabled the native screen-space probe. Look for a magenta rectangle over the game."
                : "Disabled the native screen-space probe.";
            Plugin.ChatGui.Print($"[VideoSync] {this.status}");
        }

        ImGui.InputTextMultiline("Renderer Probe", ref this.realRendererProbe, 4096, new Vector2(-1, 150), ImGuiInputTextFlags.ReadOnly);
        ImGui.TextWrapped(this.presentHookProbe.Status);
    }

    private bool PlaceWorldScreenInFrontOfPlayer()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is null)
        {
            this.worldScreenAnchor = null;
            this.status = "Could not place the world screen because no local player was found.";
            return false;
        }

        var rotation = player.Rotation;
        var forward = new Vector3(MathF.Sin(rotation), 0f, MathF.Cos(rotation));
        this.worldScreenAnchor = player.Position + (forward * this.worldScreenDistance) + new Vector3(0f, this.worldScreenHeightOffset, 0f);
        this.worldScreenRotation = rotation + MathF.PI;
        this.worldScreenElevation = 0f;
        this.worldScreenPush = 0f;
        this.status = "Placed the world screen in front of your character.";
        return true;
    }

    private void EnableNativeWorldScreen()
    {
        if (this.worldScreenAnchor is null)
        {
            this.worldScreenEnabled = false;
            this.status = "Could not spawn the screen because no world anchor was available.";
            Plugin.ChatGui.Print($"[VideoSync] {this.status}");
            return;
        }

        this.worldScreenEnabled = true;
        this.drawImguiWorldScreen = false;

        if (this.presentHookProbe.TryInstall())
        {
            this.presentHookProbe.NativeTestDrawEnabled = true;
            this.presentHookProbe.NativeScreenSpaceProbeEnabled = false;
            this.status = "Spawned the native world screen at your character. The magenta probe is off.";
        }
        else
        {
            this.presentHookProbe.NativeTestDrawEnabled = false;
            this.presentHookProbe.NativeScreenSpaceProbeEnabled = false;
            this.drawImguiWorldScreen = true;
            this.worldScreenActorOcclusion = true;
            this.status = "Placed the screen, but native D3D hooks did not install. Open the renderer diagnostics for details.";
        }

        Plugin.ChatGui.Print($"[VideoSync] {this.status}");
    }

    private void FaceWorldScreenToPlayer()
    {
        if (this.worldScreenAnchor is not { } anchor)
        {
            this.status = "Place the world screen before facing it.";
            return;
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is null)
        {
            this.status = "Could not face the world screen because no local player was found.";
            return;
        }

        var delta = player.Position - anchor;
        this.worldScreenRotation = MathF.Atan2(delta.X, delta.Z);
        this.status = "Turned the world screen toward your character.";
    }

    /// <summary>
    /// Nudges the screen relative to how the player sees it: horizontal moves it
    /// left/right on their monitor, depth moves it toward/away from them.
    /// </summary>
    private void NudgeScreenFromViewer(float horizontal, float vertical, float depth)
    {
        if (this.worldScreenAnchor is not { } anchor)
        {
            this.status = "Place the screen before fine-tuning its position.";
            return;
        }

        var side = new Vector3(MathF.Cos(this.worldScreenRotation), 0f, -MathF.Sin(this.worldScreenRotation));
        var away = Vector3.Zero;
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is not null)
        {
            var flat = anchor - player.Position;
            flat.Y = 0f;
            if (flat.LengthSquared() > 0.01f)
            {
                away = Vector3.Normalize(flat);
                side = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, flat));
            }
        }

        // Flip "right" if needed so the arrows match what the viewer sees on screen.
        if (horizontal != 0f &&
            Plugin.GameGui.WorldToScreen(anchor, out var center) &&
            Plugin.GameGui.WorldToScreen(anchor + side, out var offsetPoint) &&
            offsetPoint.X < center.X)
        {
            side = -side;
        }

        this.worldScreenAnchor = anchor
            + (side * horizontal)
            + new Vector3(0f, vertical, 0f)
            + (away * depth);
        this.worldScreenElevation += vertical;
        this.worldScreenPush += depth;
        this.QueueSnowSyncBroadcast();
    }

    private Vector3 GetViewerAwayAxis()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is not null && this.worldScreenAnchor is { } anchor)
        {
            var flat = anchor - player.Position;
            flat.Y = 0f;
            if (flat.LengthSquared() > 0.01f)
            {
                return Vector3.Normalize(flat);
            }
        }

        return new Vector3(MathF.Sin(this.worldScreenRotation), 0f, MathF.Cos(this.worldScreenRotation));
    }

    private IReadOnlyList<NativeDecorQuad> BuildWorldScreenDecorations(
        Vector3 anchor,
        Vector3 right,
        Vector3 up,
        float halfWidth,
        float halfHeight)
    {
        var quads = new List<NativeDecorQuad>(16);

        if (this.ambientGlowEnabled)
        {
            var spread = Math.Clamp(this.ambientGlowSize, 0.05f, 1.2f);
            var alpha = Math.Clamp(this.ambientGlowIntensity, 0.05f, 0.85f);
            var color = this.ambientGlowColor;
            AddRectQuad(
                quads,
                anchor,
                right,
                up,
                -halfWidth - (spread * 1.45f),
                halfWidth + (spread * 1.45f),
                -halfHeight - (spread * 1.1f),
                halfHeight + (spread * 1.1f),
                new Vector4(color.X, color.Y, color.Z, alpha * 0.22f),
                0.16f);
            AddRectQuad(
                quads,
                anchor,
                right,
                up,
                -halfWidth - spread,
                halfWidth + spread,
                -halfHeight - (spread * 0.72f),
                halfHeight + (spread * 0.72f),
                new Vector4(color.X, color.Y, color.Z, alpha * 0.36f),
                0.14f);
        }

        var t = Math.Clamp(this.frameThickness, 0.04f, 0.42f);
        switch (this.tvFrameStyle)
        {
            case 1:
                AddFrameBands(quads, anchor, right, up, halfWidth, halfHeight, t, new Vector4(0.30f, 0.15f, 0.07f, 1f));
                AddFrameBands(quads, anchor, right, up, halfWidth + (t * 0.08f), halfHeight + (t * 0.08f), t * 0.22f, new Vector4(0.72f, 0.48f, 0.22f, 1f));
                AddRectQuad(quads, anchor, right, up, -halfWidth - (t * 1.35f), halfWidth + (t * 1.35f), -halfHeight - (t * 1.55f), -halfHeight - (t * 0.95f), new Vector4(0.14f, 0.07f, 0.035f, 1f), 0.11f);
                break;
            case 2:
                AddFrameBands(quads, anchor, right, up, halfWidth, halfHeight, t, new Vector4(0.10f, 0.12f, 0.14f, 1f));
                AddFrameBands(quads, anchor, right, up, halfWidth + (t * 0.18f), halfHeight + (t * 0.18f), t * 0.18f, new Vector4(0.34f, 0.58f, 0.74f, 1f));
                AddCornerPlates(quads, anchor, right, up, halfWidth, halfHeight, t, new Vector4(0.20f, 0.23f, 0.26f, 1f));
                break;
            case 3:
                AddFrameBands(quads, anchor, right, up, halfWidth, halfHeight, t, new Vector4(0.015f, 0.016f, 0.020f, 1f));
                AddRectQuad(quads, anchor, right, up, -halfWidth - (t * 1.38f), halfWidth + (t * 1.38f), halfHeight + (t * 1.02f), halfHeight + (t * 1.34f), new Vector4(0.05f, 0.85f, 1.0f, 1f), 0.10f);
                AddRectQuad(quads, anchor, right, up, -halfWidth - (t * 1.38f), halfWidth + (t * 1.38f), -halfHeight - (t * 1.34f), -halfHeight - (t * 1.02f), new Vector4(1.0f, 0.12f, 0.72f, 1f), 0.10f);
                AddRectQuad(quads, anchor, right, up, -halfWidth - (t * 1.34f), -halfWidth - (t * 1.02f), -halfHeight - (t * 1.15f), halfHeight + (t * 1.15f), new Vector4(0.65f, 0.28f, 1.0f, 1f), 0.10f);
                AddRectQuad(quads, anchor, right, up, halfWidth + (t * 1.02f), halfWidth + (t * 1.34f), -halfHeight - (t * 1.15f), halfHeight + (t * 1.15f), new Vector4(0.06f, 1.0f, 0.70f, 1f), 0.10f);
                break;
            case 4:
                AddFrameBands(quads, anchor, right, up, halfWidth, halfHeight, t, new Vector4(0.05f, 0.20f, 0.18f, 1f));
                AddFrameBands(quads, anchor, right, up, halfWidth + (t * 0.16f), halfHeight + (t * 0.16f), t * 0.20f, new Vector4(0.86f, 0.64f, 0.28f, 1f));
                AddCornerPlates(quads, anchor, right, up, halfWidth, halfHeight, t * 1.15f, new Vector4(0.60f, 0.42f, 0.18f, 1f));
                AddRectQuad(quads, anchor, right, up, -halfWidth * 0.42f, halfWidth * 0.42f, halfHeight + (t * 1.12f), halfHeight + (t * 1.36f), new Vector4(0.12f, 0.95f, 0.78f, 1f), 0.10f);
                break;
        }

        return quads;
    }

    private static void AddFrameBands(
        List<NativeDecorQuad> quads,
        Vector3 anchor,
        Vector3 right,
        Vector3 up,
        float halfWidth,
        float halfHeight,
        float thickness,
        Vector4 color)
    {
        var outerWidth = halfWidth + thickness;
        var outerHeight = halfHeight + thickness;
        AddRectQuad(quads, anchor, right, up, -outerWidth, outerWidth, halfHeight, outerHeight, color, 0.11f);
        AddRectQuad(quads, anchor, right, up, -outerWidth, outerWidth, -outerHeight, -halfHeight, color, 0.11f);
        AddRectQuad(quads, anchor, right, up, -outerWidth, -halfWidth, -halfHeight, halfHeight, color, 0.11f);
        AddRectQuad(quads, anchor, right, up, halfWidth, outerWidth, -halfHeight, halfHeight, color, 0.11f);
    }

    private static void AddCornerPlates(
        List<NativeDecorQuad> quads,
        Vector3 anchor,
        Vector3 right,
        Vector3 up,
        float halfWidth,
        float halfHeight,
        float thickness,
        Vector4 color)
    {
        var outerWidth = halfWidth + (thickness * 1.45f);
        var innerWidth = halfWidth + (thickness * 0.28f);
        var outerHeight = halfHeight + (thickness * 1.45f);
        var innerHeight = halfHeight + (thickness * 0.28f);
        AddRectQuad(quads, anchor, right, up, -outerWidth, -innerWidth, innerHeight, outerHeight, color, 0.09f);
        AddRectQuad(quads, anchor, right, up, innerWidth, outerWidth, innerHeight, outerHeight, color, 0.09f);
        AddRectQuad(quads, anchor, right, up, -outerWidth, -innerWidth, -outerHeight, -innerHeight, color, 0.09f);
        AddRectQuad(quads, anchor, right, up, innerWidth, outerWidth, -outerHeight, -innerHeight, color, 0.09f);
    }

    private static void AddRectQuad(
        List<NativeDecorQuad> quads,
        Vector3 anchor,
        Vector3 right,
        Vector3 up,
        float left,
        float rightEdge,
        float bottom,
        float top,
        Vector4 color,
        float depthOffset)
    {
        quads.Add(new NativeDecorQuad(
            anchor + (right * left) + (up * top),
            anchor + (right * rightEdge) + (up * top),
            anchor + (right * rightEdge) + (up * bottom),
            anchor + (right * left) + (up * bottom),
            color,
            depthOffset));
    }

}
