using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace VideoSyncPrototype.Windows;

public sealed class VideoSurfaceWindow : Window
{
    private readonly MainWindow owner;

    public VideoSurfaceWindow(MainWindow owner)
        : base(
            "Video Sync Screen###VideoSyncSurface",
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse)
    {
        this.owner = owner;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320, 220),
            MaximumSize = new Vector2(1920, 1200),
        };
        this.Size = new Vector2(720, 450);
    }

    public override void Draw()
    {
        this.owner.DrawScreenSurface();
    }
}
