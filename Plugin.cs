using System;
using System.IO;
using System.Text.RegularExpressions;
using Dalamud.Game.Chat;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using VideoSyncPrototype.Windows;

namespace VideoSyncPrototype;

public sealed class Plugin : IDalamudPlugin
{
    private static readonly string[] CommandNames = ["/lilly", "/pad"];

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private static readonly Regex SyncCodeRegex = new(@"(?:VS2:[A-Za-z0-9_-]{16,}|F14YT1-[A-Za-z0-9_-]+)", RegexOptions.Compiled);
    private static readonly Regex Watch2GetherRegex = new(@"(?:W2G1:[A-Za-z0-9_-]{16,}|https?://w2g\.tv/[^\s<>""]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LegacySnowSyncRegex = new(@"VSYNC1:(F14YT1-[A-Za-z0-9_-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly MainWindow mainWindow;
    private readonly MapMarkers.MapMarkerService mapMarkerService = new();
    private readonly WindowSystem windowSystem = new("VideoSyncPrototype");
    internal static GameChatSender ChatSender { get; private set; } = null!;
    internal static Configuration Config { get; private set; } = null!;

    public Plugin()
    {
        ChatSender = new GameChatSender();
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        var pluginDirectory = PluginInterface.AssemblyLocation.Directory?.FullName ?? Directory.GetCurrentDirectory();
        this.mainWindow = new MainWindow(pluginDirectory, Config);
        this.windowSystem.AddWindow(this.mainWindow);
        this.windowSystem.AddWindow(this.mainWindow.SurfaceWindow);

        foreach (var commandName in CommandNames)
        {
            CommandManager.AddHandler(commandName, new CommandInfo(this.OnCommand)
            {
                HelpMessage = "Open Lillypad Toolkit.",
            });
        }

        PluginInterface.UiBuilder.Draw += this.Draw;
        PluginInterface.UiBuilder.OpenMainUi += this.OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += this.OpenMainUi;
        ChatGui.ChatMessage += this.OnChatMessage;

        Log.Information("Lillypad Toolkit loaded.");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= this.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= this.OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= this.OpenMainUi;
        ChatGui.ChatMessage -= this.OnChatMessage;
        foreach (var commandName in CommandNames)
        {
            CommandManager.RemoveHandler(commandName);
        }
        this.windowSystem.RemoveAllWindows();
        this.mainWindow.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        this.OpenMainUi();
    }

    private void OpenMainUi()
    {
        this.mainWindow.IsOpen = true;
    }

    private void Draw()
    {
        this.windowSystem.Draw();
        this.mainWindow.DrawWorldSurfaceOverlay();
        this.mapMarkerService.Draw(Config);
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        var text = message.Message.TextValue;
        var watch2GetherMatch = Watch2GetherRegex.Match(text);
        if (watch2GetherMatch.Success &&
            Watch2GetherRoomParser.TryParse(watch2GetherMatch.Value, out var room))
        {
            this.mainWindow.ReceiveWatch2GetherRoom(room);
            return;
        }

        var legacyMatch = LegacySnowSyncRegex.Match(text);
        if (legacyMatch.Success)
        {
            this.mainWindow.ReceiveSnowSync(legacyMatch.Groups[1].Value);
            return;
        }

        var match = SyncCodeRegex.Match(text);
        if (!match.Success)
        {
            return;
        }

        // "[VideoSync]" marks a code someone shared on purpose; a bare code is a
        // live sync broadcast from a running watch party.
        if (text.Contains("[VideoSync]", StringComparison.OrdinalIgnoreCase))
        {
            this.mainWindow.ReceiveSharedCode(match.Value);
        }
        else
        {
            this.mainWindow.ReceiveSnowSync(match.Value);
        }
    }
}
