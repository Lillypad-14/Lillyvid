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
    // Style tab.

    private void DrawStyleTab()
    {
        ImGui.Spacing();
        UiTheme.SectionTitle("TV look");
        ImGui.Spacing();

        if (ImGui.BeginCombo("Frame", FrameStyleNames[Math.Clamp(this.tvFrameStyle, 0, FrameStyleNames.Length - 1)]))
        {
            for (var i = 0; i < FrameStyleNames.Length; i++)
            {
                var selected = this.tvFrameStyle == i;
                if (ImGui.Selectable(FrameStyleNames[i], selected))
                {
                    this.tvFrameStyle = i;
                    this.status = i == 0 ? "Using the generic frameless TV." : $"Using {FrameStyleNames[i]} frame.";
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        UiTheme.PushSliderAccent();
        if (ImGui.SliderFloat("Frame thickness", ref this.frameThickness, 0.04f, 0.42f, "%.2f yalms"))
        {
            this.frameThickness = Math.Clamp(this.frameThickness, 0.04f, 0.42f);
        }

        UiTheme.PopSliderAccent();

        ImGui.Spacing();
        UiTheme.SectionTitle("Ambient glow");
        ImGui.Spacing();
        ImGui.Checkbox("Glow", ref this.ambientGlowEnabled);
        if (this.ambientGlowEnabled)
        {
            ImGui.ColorEdit4("Glow color", ref this.ambientGlowColor);
            UiTheme.PushSliderAccent();
            ImGui.SliderFloat("Glow intensity", ref this.ambientGlowIntensity, 0.05f, 0.85f, "%.2f");
            ImGui.SliderFloat("Glow spread", ref this.ambientGlowSize, 0.05f, 1.2f, "%.2f yalms");
            UiTheme.PopSliderAccent();
        }

        ImGui.Spacing();
        UiTheme.SectionTitle("Cinema presets");
        ImGui.Spacing();

        if (ImGui.BeginCombo("Preset", CinemaPresetNames[Math.Clamp(this.cinemaPresetIndex, 0, CinemaPresetNames.Length - 1)]))
        {
            for (var i = 0; i < CinemaPresetNames.Length; i++)
            {
                var selected = this.cinemaPresetIndex == i;
                if (ImGui.Selectable(CinemaPresetNames[i], selected))
                {
                    this.cinemaPresetIndex = i;
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        if (UiTheme.PrimaryButton("Apply preset"))
        {
            this.ApplyCinemaPreset(this.cinemaPresetIndex);
        }

        ImGui.SameLine();
        if (ImGui.Button("Generic TV"))
        {
            this.ApplyCinemaPreset(0);
        }
    }

    private void ApplyCinemaPreset(int index)
    {
        this.cinemaPresetIndex = Math.Clamp(index, 0, CinemaPresetNames.Length - 1);
        switch (this.cinemaPresetIndex)
        {
            case 0:
                this.tvFrameStyle = 0;
                this.ambientGlowEnabled = false;
                this.worldScreenLockAspect = true;
                this.worldScreenWidth = 4.0f;
                this.worldScreenHeight = 2.25f;
                this.frameThickness = 0.16f;
                this.audioRange = 30f;
                break;
            case 1:
                this.tvFrameStyle = 1;
                this.ambientGlowEnabled = true;
                this.ambientGlowColor = new Vector4(1.0f, 0.63f, 0.28f, 0.34f);
                this.ambientGlowIntensity = 0.30f;
                this.ambientGlowSize = 0.34f;
                this.worldScreenLockAspect = true;
                this.worldScreenWidth = 4.8f;
                this.worldScreenHeight = this.worldScreenWidth * 9f / 16f;
                this.frameThickness = 0.20f;
                this.audioRange = 28f;
                break;
            case 2:
                this.tvFrameStyle = 3;
                this.ambientGlowEnabled = true;
                this.ambientGlowColor = new Vector4(0.15f, 0.85f, 1.0f, 0.48f);
                this.ambientGlowIntensity = 0.52f;
                this.ambientGlowSize = 0.58f;
                this.worldScreenLockAspect = true;
                this.worldScreenWidth = 5.6f;
                this.worldScreenHeight = this.worldScreenWidth * 9f / 16f;
                this.frameThickness = 0.10f;
                this.audioRange = 45f;
                break;
            case 3:
                this.tvFrameStyle = 2;
                this.ambientGlowEnabled = true;
                this.ambientGlowColor = new Vector4(0.34f, 0.66f, 1.0f, 0.36f);
                this.ambientGlowIntensity = 0.38f;
                this.ambientGlowSize = 0.62f;
                this.worldScreenLockAspect = true;
                this.worldScreenWidth = 7.0f;
                this.worldScreenHeight = this.worldScreenWidth * 9f / 16f;
                this.frameThickness = 0.24f;
                this.audioRange = 70f;
                break;
            case 4:
                this.tvFrameStyle = 4;
                this.ambientGlowEnabled = true;
                this.ambientGlowColor = new Vector4(0.12f, 0.95f, 0.78f, 0.42f);
                this.ambientGlowIntensity = 0.44f;
                this.ambientGlowSize = 0.46f;
                this.worldScreenLockAspect = true;
                this.worldScreenWidth = 6.2f;
                this.worldScreenHeight = this.worldScreenWidth * 9f / 16f;
                this.frameThickness = 0.18f;
                this.audioRange = 42f;
                break;
        }

        this.lastSentVolume = -1f;
        this.lastAudioWriteUtc = DateTime.MinValue;
        this.status = $"Applied {CinemaPresetNames[this.cinemaPresetIndex]} preset.";
    }

    // Turns the friendly upscaling preset into the renderer's (filter, sharpen) pair.
    // Off is (bilinear, 0) — byte-identical to the original screen.
    private (int Filter, float Sharpness) ResolveUpscale()
    {
        return this.upscaleMode switch
        {
            0 => (0, 0f),      // Off — the original bilinear look
            1 => (0, 0.15f),   // Fast — bilinear + light sharpen
            2 => (1, 0f),      // Balanced — bicubic
            3 => (2, 0f),      // Quality — Lanczos
            4 => (2, 0.40f),   // Ultra — Lanczos + strong sharpen
            _ => (Math.Clamp(this.upscaleFilter, 0, 2), Math.Clamp(this.upscaleSharpness, 0f, 1f)),
        };
    }

}
