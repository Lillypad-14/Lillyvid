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
    // Sync backend: Snowcloak/watch2gether room + payload creation, receive/broadcast/apply, playback control.

    private string GetSelectedSnowSyncshellLabel()
    {
        if (this.selectedSnowSyncshellIndex >= 0 && this.selectedSnowSyncshellIndex < this.snowSyncshells.Count)
        {
            return FormatSnowSyncshellLabel(this.snowSyncshells[this.selectedSnowSyncshellIndex]);
        }

        return int.TryParse(this.syncshellText, out var shellNumber)
            ? $"Manual /ss{shellNumber}"
            : "Choose a syncshell";
    }

    private string GetSyncTransportLabel()
    {
        return this.syncTransportIndex == SyncTransportCwls
            ? FormatCwlsLabel(this.selectedCwlIndex)
            : int.TryParse(this.syncshellText, out var shellNumber) ? $"/ss{shellNumber}" : "Snowcloak";
    }

    private static string FormatCwlsLabel(int index)
    {
        var name = GameChat.GetCrossworldLinkshellName(index + 1);
        return name is null
            ? $"CWLS {index + 1} (no linkshell)"
            : $"{index + 1}: {name}";
    }

    private bool TryGetSyncChatPrefix(out string commandPrefix, out string label, out string error)
    {
        commandPrefix = string.Empty;
        label = string.Empty;
        error = string.Empty;

        if (this.syncTransportIndex == SyncTransportCwls)
        {
            var cwlNumber = this.selectedCwlIndex + 1;
            if (cwlNumber is < 1 or > GameChat.CrossworldLinkshellSlots)
            {
                error = "CWLS slot must be 1-8. Use the number from /cwl1 through /cwl8.";
                return false;
            }

            if (GameChat.GetCrossworldLinkshellName(cwlNumber) is null)
            {
                error = $"You have no cross-world linkshell in slot {cwlNumber}. Pick one that has a name.";
                return false;
            }

            commandPrefix = $"/cwlinkshell{cwlNumber}";
            label = FormatCwlsLabel(this.selectedCwlIndex);
            return true;
        }

        if (!int.TryParse(this.syncshellText, out var shellNumber) || shellNumber is < 1 or > 50)
        {
            error = "Snowcloak syncshell number must be 1-50.";
            return false;
        }

        commandPrefix = $"/ss{shellNumber}";
        label = $"Snowcloak /ss{shellNumber}";
        return true;
    }

    private void RefreshSnowSyncshells()
    {
        try
        {
            var shells = ReadSnowSyncshellsFromConfig();
            this.snowSyncshells.Clear();
            this.snowSyncshells.AddRange(shells);
            this.selectedSnowSyncshellIndex = this.FindSnowSyncshellIndex(this.syncshellText);

            if (this.selectedSnowSyncshellIndex < 0)
            {
                var firstEnabled = this.snowSyncshells.FindIndex(shell => shell.Enabled);
                if (firstEnabled >= 0)
                {
                    this.selectedSnowSyncshellIndex = firstEnabled;
                    this.syncshellText = this.snowSyncshells[firstEnabled].ShellNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            this.lastSnowSyncshellRefreshUtc = DateTime.UtcNow;
            this.snowSyncshellStatus = this.snowSyncshells.Count == 0
                ? "No Snowcloak syncshells found. You can still type a manual /ss number."
                : $"Loaded {this.snowSyncshells.Count} Snowcloak syncshell(s).";
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not refresh Snowcloak syncshell list.");
            this.lastSnowSyncshellRefreshUtc = DateTime.UtcNow;
            this.snowSyncshellStatus = "Could not read Snowcloak syncshells. Manual /ss# still works.";
        }
    }

    private int FindSnowSyncshellIndex(string shellText)
    {
        if (!int.TryParse(shellText, out var shellNumber))
        {
            return -1;
        }

        return this.snowSyncshells.FindIndex(shell => shell.ShellNumber == shellNumber);
    }

    private static string FormatSnowSyncshellLabel(SnowSyncshell shell)
    {
        return shell.Enabled
            ? $"{shell.Gid} (/ss{shell.ShellNumber})"
            : $"{shell.Gid} (/ss{shell.ShellNumber}, disabled)";
    }

    private static List<SnowSyncshell> ReadSnowSyncshellsFromConfig()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dbPath = Path.Combine(appData, "XIVLauncher", "pluginConfigs", "Snowcloak", "Snowcloak.sqlite");
        if (!File.Exists(dbPath))
        {
            return [];
        }

        var snowcloakDirectory = FindSnowcloakInstallDirectory(appData);
        if (snowcloakDirectory is null)
        {
            return [];
        }

        string payload;
        try
        {
            payload = ReadSnowcloakStateDocument(snowcloakDirectory, dbPath, "syncshells.json");
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Falling back to raw Snowcloak syncshell scan.");
            payload = TryReadSnowSyncshellJsonByScanningSqlite(dbPath);
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            return [];
        }

        var shells = new List<SnowSyncshell>();
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("ServerShellStorage", out var serverStorage) ||
            serverStorage.ValueKind != JsonValueKind.Object)
        {
            return shells;
        }

        foreach (var server in serverStorage.EnumerateObject())
        {
            if (!server.Value.TryGetProperty("GidShellConfig", out var gidConfig) ||
                gidConfig.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var shellProperty in gidConfig.EnumerateObject())
            {
                var shellElement = shellProperty.Value;
                if (!shellElement.TryGetProperty("ShellNumber", out var shellNumberElement) ||
                    !shellNumberElement.TryGetInt32(out var shellNumber))
                {
                    continue;
                }

                var enabled = shellElement.TryGetProperty("Enabled", out var enabledElement) &&
                              enabledElement.ValueKind == JsonValueKind.True;
                shells.Add(new SnowSyncshell(shellProperty.Name, shellNumber, enabled));
            }
        }

        shells.Sort((left, right) =>
        {
            var enabledCompare = right.Enabled.CompareTo(left.Enabled);
            if (enabledCompare != 0)
            {
                return enabledCompare;
            }

            var numberCompare = left.ShellNumber.CompareTo(right.ShellNumber);
            return numberCompare != 0
                ? numberCompare
                : string.Compare(left.Gid, right.Gid, StringComparison.OrdinalIgnoreCase);
        });
        return shells;
    }

    private static string? FindSnowcloakInstallDirectory(string appData)
    {
        var root = Path.Combine(appData, "XIVLauncher", "installedPlugins", "Snowcloak");
        if (!Directory.Exists(root))
        {
            return null;
        }

        return Directory.EnumerateDirectories(root)
            .OrderByDescending(ParseDirectoryVersion)
            .FirstOrDefault(path => File.Exists(Path.Combine(path, "Microsoft.Data.Sqlite.dll")));
    }

    private static Version ParseDirectoryVersion(string path)
    {
        return Version.TryParse(Path.GetFileName(path), out var version)
            ? version
            : new Version(0, 0);
    }

    private static string ReadSnowcloakStateDocument(string snowcloakDirectory, string dbPath, string documentName)
    {
        var sqlitePath = Path.Combine(snowcloakDirectory, "Microsoft.Data.Sqlite.dll");
        if (!File.Exists(sqlitePath))
        {
            return string.Empty;
        }

        var oldPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        ResolveEventHandler resolver = (_, args) =>
        {
            var assemblyName = new AssemblyName(args.Name).Name;
            if (string.IsNullOrWhiteSpace(assemblyName))
            {
                return null;
            }

            var candidate = Path.Combine(snowcloakDirectory, assemblyName + ".dll");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };

        AppDomain.CurrentDomain.AssemblyResolve += resolver;
        Environment.SetEnvironmentVariable("PATH", snowcloakDirectory + Path.PathSeparator + oldPath);
        try
        {
            var sqliteAssembly = Assembly.LoadFrom(sqlitePath);
            var connectionType = sqliteAssembly.GetType("Microsoft.Data.Sqlite.SqliteConnection", throwOnError: true)!;
            using var connection = (IDisposable)Activator.CreateInstance(
                connectionType,
                $"Data Source={dbPath};Mode=ReadOnly")!;
            connectionType.GetMethod("Open", Type.EmptyTypes)!.Invoke(connection, null);

            using var command = (IDisposable)connectionType.GetMethod("CreateCommand", Type.EmptyTypes)!.Invoke(connection, null)!;
            var commandType = command.GetType();
            commandType.GetProperty("CommandText")!.SetValue(
                command,
                "select payload from state_documents where document_name = $documentName");
            var parameters = commandType.GetProperty("Parameters")!.GetValue(command)!;
            var addWithValue = parameters.GetType().GetMethod("AddWithValue", [typeof(string), typeof(object)])!;
            addWithValue.Invoke(parameters, ["$documentName", documentName]);

            using var reader = (IDisposable)commandType.GetMethod("ExecuteReader", Type.EmptyTypes)!.Invoke(command, null)!;
            var readerType = reader.GetType();
            var read = (bool)readerType.GetMethod("Read", Type.EmptyTypes)!.Invoke(reader, null)!;
            if (!read)
            {
                return string.Empty;
            }

            return (string)readerType.GetMethod("GetString", [typeof(int)])!.Invoke(reader, [0])!;
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            AppDomain.CurrentDomain.AssemblyResolve -= resolver;
        }
    }

    private static string TryReadSnowSyncshellJsonByScanningSqlite(string dbPath)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(dbPath);
        }
        catch (IOException)
        {
            return string.Empty;
        }

        var text = Encoding.UTF8.GetString(bytes);
        var searchIndex = 0;
        while (true)
        {
            var markerIndex = text.IndexOf("\"ServerShellStorage\"", searchIndex, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                return string.Empty;
            }

            var objectStart = text.LastIndexOf('{', markerIndex);
            if (objectStart < 0)
            {
                return string.Empty;
            }

            var candidate = ExtractJsonObject(text, objectStart);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                try
                {
                    using var _ = JsonDocument.Parse(candidate);
                    return candidate;
                }
                catch (JsonException)
                {
                    // Keep scanning; SQLite can retain stale fragments in free pages.
                }
            }

            searchIndex = markerIndex + 1;
        }
    }

    private static string ExtractJsonObject(string text, int objectStart)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = objectStart; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == '{')
            {
                depth++;
                continue;
            }

            if (c != '}')
            {
                continue;
            }

            depth--;
            if (depth == 0)
            {
                return text.Substring(objectStart, i - objectStart + 1);
            }
        }

        return string.Empty;
    }

    public void ReceiveSharedCode(string code)
    {
        // Our own share arrives back as a chat echo; do not re-open on it.
        if (string.Equals(code, this.lastOutboundSnowSyncCode, StringComparison.Ordinal)
            && DateTime.UtcNow - this.lastOutboundSnowSyncUtc < TimeSpan.FromSeconds(8))
        {
            return;
        }

        if (!TryDecode(code, out var payload, out var error))
        {
            this.status = $"Ignored invalid shared code: {error}";
            return;
        }

        this.pasteCode = code;
        this.decodedSummary = FormatSummary(payload);
        this.status = "Received a sync code from chat. Use Open Pasted or Open Overlay to join it.";
        this.IsOpen = true;
    }

    public void ReceiveSnowSync(string code)
    {
        this.ReceiveSnowSync(code, force: false);
    }

    private void ReceiveSnowSync(string code, bool force)
    {
        if (!force &&
            string.Equals(code, this.lastOutboundSnowSyncCode, StringComparison.Ordinal)
            && DateTime.UtcNow - this.lastOutboundSnowSyncUtc < TimeSpan.FromSeconds(8))
        {
            return;
        }

        if (!TryDecode(code, out var payload, out var error))
        {
            this.status = $"Ignored invalid chat video sync: {error}";
            return;
        }

        var running = this.rendererProcess is not null && !this.rendererProcess.HasExited;
        var sameVideo = running && string.Equals(this.currentVideoId, payload.VideoId, StringComparison.Ordinal);

        // Corrections for the party we are already in always apply; anything else
        // only becomes a "watch party found" offer unless the user opted into
        // joining automatically.
        if (!force && !sameVideo && !this.autoJoinSnowSync)
        {
            var isNewDiscovery = this.discoveredParty is not { } known
                || !string.Equals(known.VideoId, payload.VideoId, StringComparison.Ordinal)
                || DateTime.UtcNow - this.discoveredPartyUtc > TimeSpan.FromSeconds(90);
            this.discoveredParty = payload;
            this.discoveredPartyCode = code;
            this.discoveredPartyUtc = DateTime.UtcNow;

            if (isNewDiscovery)
            {
                this.status = "Found a watch party in your linkshell. Join it from the Watch tab.";
                Plugin.ChatGui.Print("[VideoSync] A watch party is running in your linkshell. Type /lilly or /pad to join.");
            }

            return;
        }

        this.ClearDiscoveredParty();
        this.pasteCode = code;
        this.decodedSummary = FormatSummary(payload);
        this.ApplyRemoteSync(payload, "linkshell");
    }

    private void ClearDiscoveredParty()
    {
        this.discoveredParty = null;
        this.discoveredPartyCode = string.Empty;
        this.discoveredPartyUtc = DateTime.MinValue;
    }

    public void ReceiveWatch2GetherRoom(Watch2GetherRoom room)
    {
        var key = room.NormalizedUrl;
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (string.Equals(key, this.lastOutboundWatch2GetherRoomKey, StringComparison.OrdinalIgnoreCase)
            && DateTime.UtcNow - this.lastOutboundWatch2GetherRoomUtc < TimeSpan.FromSeconds(8))
        {
            return;
        }

        if (this.ignoredWatch2GetherRoomKeys.Contains(key) ||
            string.Equals(key, this.incomingWatch2GetherRoomKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        this.incomingWatch2GetherRoom = room;
        this.incomingWatch2GetherRoomKey = key;
        this.incomingWatch2GetherRoomUtc = DateTime.UtcNow;
        this.status = "A shared Watch2Gether room was created. Open it from the Watch tab.";
        Plugin.ChatGui.Print("[VideoSync] A shared video room was created. Type /lilly or /pad to open it.");
        this.IsOpen = true;
    }

    private void ClearIncomingWatch2GetherRoom()
    {
        this.incomingWatch2GetherRoom = null;
        this.incomingWatch2GetherRoomKey = string.Empty;
        this.incomingWatch2GetherRoomUtc = DateTime.MinValue;
    }

    private void CreateMovieRoom()
    {
        if (this.creatingWatch2GetherRoom)
        {
            return;
        }

        var apiKey = this.config.Watch2GetherApiKey.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            this.status = "Add your Watch2Gether API key in Settings first.";
            return;
        }

        // Safety net: if this key is being used to create a room, make sure it's on
        // disk (the field-edit handler already saves, but persist here too so a key
        // that works is never lost across restarts).
        if (this.config.Watch2GetherApiKey != apiKey)
        {
            this.config.Watch2GetherApiKey = apiKey;
        }

        this.config.Save();

        if (!Uri.TryCreate(this.youtubeUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            this.status = "Paste a valid video link first (a full http/https URL).";
            return;
        }

        // Kick off the API call on a background task and poll it from the UI thread in
        // TickWatch2GetherRoomCreation. One POST creates the room and preloads the
        // video server-side, so there's no page to scrape and no popup to close.
        this.creatingWatch2GetherRoom = true;
        this.status = "Creating room...";
        this.watch2GetherCreateTask = Watch2GetherApi.CreateRoomAsync(apiKey, uri.ToString());
    }

    private void TickWatch2GetherRoomCreation()
    {
        if (this.watch2GetherCreateTask is not { IsCompleted: true } task)
        {
            return;
        }

        this.watch2GetherCreateTask = null;
        this.creatingWatch2GetherRoom = false;

        if (task.IsCompletedSuccessfully)
        {
            var room = task.Result;
            this.SaveWatch2GetherRoom(room);

            // Auto-open on the in-world screen so the host is one click into watching.
            // The video is already the room's current item, so it autoplays.
            this.PlayWatch2GetherRoomInWorld(room);
            this.status = "Room ready. Video is loading on the screen — share the code below.";
        }
        else
        {
            var message = task.Exception?.InnerException is Watch2GetherApiException apiEx
                ? apiEx.Message
                : "Failed to create the room. Please try again.";
            this.status = message;
        }
    }

    private void SetActionStatus(string message)
    {
        this.status = message;
        Plugin.ChatGui.Print($"[VideoSync] {message}");
    }

    private void SendWatch2GetherRoom(Watch2GetherRoom room, bool openInWorld = true)
    {
        this.SaveWatch2GetherRoom(room);
        if (openInWorld)
        {
            this.PlayWatch2GetherRoomInWorld(room);
        }

        if (!this.TryGetSyncChatPrefix(out var commandPrefix, out var label, out var error))
        {
            this.SetActionStatus($"Created the Watch2Gether room and opened it on the TV, but could not share it: {error}");
            return;
        }

        var code = this.BuildRoomShareCode(room);
        if (!this.TrySendChatCommand($"{commandPrefix} [VideoSync-W2G] {code}"))
        {
            this.SetActionStatus("Created the Watch2Gether room and opened it on the TV, but the chat invite could not be sent.");
            return;
        }

        var key = room.NormalizedUrl;
        this.lastOutboundWatch2GetherRoomKey = key;
        this.lastOutboundWatch2GetherRoomUtc = DateTime.UtcNow;
        this.status = $"Created and shared a Watch2Gether room through {label}.";
    }

    private void SaveWatch2GetherRoom(Watch2GetherRoom room)
    {
        this.lastWatch2GetherRoomUrl = room.NormalizedUrl;
        this.lastWatch2GetherRoomCode = this.BuildRoomShareCode(room);
        this.lastSentVolume = -1f;
        this.lastAudioWriteUtc = DateTime.MinValue;
    }

    private void OpenPastedWatch2GetherRoom()
    {
        if (!Watch2GetherRoomParser.TryParse(this.pasteWatch2GetherRoomCode, out var room))
        {
            this.SetActionStatus("Paste a valid W2G join code or Watch2Gether room URL first.");
            return;
        }

        this.SaveWatch2GetherRoom(room);
        this.PlayWatch2GetherRoomInWorld(room);
    }

    private void PlayWatch2GetherRoomInWorld(Watch2GetherRoom room)
    {
        if (!this.StartRendererBridge(room.NormalizedUrl))
        {
            return;
        }

        this.currentVideoId = string.Empty;
        var matchedHostLayout = false;
        if (room.Layout is { } layout)
        {
            // The join code carried the host's exact placement — reproduce it so the
            // joiner's TV matches position, size, stretch, and occlusion 1:1, even if
            // this client already had a screen up from a previous room.
            this.ApplyRoomScreenLayout(layout);
            matchedHostLayout = true;
        }
        else if (!this.PlaceWorldScreenInFrontOfPlayer())
        {
            return;
        }

        this.EnableNativeWorldScreen();
        this.playingWatch2GetherRoom = true;
        this.ignoredWatch2GetherRoomKeys.Add(room.NormalizedUrl);
        this.status = matchedHostLayout
            ? "Joined the room and matched the host's screen — same spot, size, and stretch."
            : "Opened the Watch2Gether room on the in-world screen.";
    }

    private void OpenWatch2GetherRoom(Watch2GetherRoom room)
    {
        Process.Start(new ProcessStartInfo(room.NormalizedUrl) { UseShellExecute = true });
        this.ignoredWatch2GetherRoomKeys.Add(room.NormalizedUrl);
        this.status = "Opened the shared Watch2Gether room in your browser.";
    }

    private void TestReceiveSnowSync()
    {
        string code;
        if (TryDecode(this.pasteCode, out _, out _))
        {
            code = this.pasteCode.Trim();
        }
        else if (TryDecode(this.generatedCode, out _, out _))
        {
            code = this.generatedCode.Trim();
        }
        else if (this.TryCreateCurrentSyncPayload(out var payload, out var error))
        {
            code = SyncCode.Encode(payload);
        }
        else
        {
            this.status = $"Could not create a test receive payload: {error}";
            return;
        }

        this.StopPlayback();
        this.worldScreenAnchor = null;
        this.currentVideoId = string.Empty;
        this.ReceiveSnowSync(code, force: true);
        this.status = "Simulated a fresh incoming chat VSYNC locally. " + this.status;
    }

    private void ShareViaSnowcloak(string code)
    {
        if (!this.TryGetSyncChatPrefix(out var commandPrefix, out var label, out var error))
        {
            this.status = error;
            return;
        }

        if (!this.TrySendChatCommand($"{commandPrefix} [VideoSync] {code}"))
        {
            return;
        }

        this.lastOutboundSnowSyncCode = code.Trim();
        this.lastOutboundSnowSyncUtc = DateTime.UtcNow;
        this.status = $"Shared sync code through {label}.";
    }

    private void SendSyncTransportTest()
    {
        if (!this.TryGetSyncChatPrefix(out var commandPrefix, out var label, out var error))
        {
            this.status = error;
            return;
        }

        if (this.TrySendChatCommand($"{commandPrefix} [VideoSync] channel test"))
        {
            this.status = $"Sent a test message through {label}.";
        }
    }

    private void BroadcastCurrentSnowSync(string reason)
    {
        if (!this.TryCreateCurrentSyncPayload(out var payload, out var error))
        {
            this.status = error;
            return;
        }

        if (!this.TryGetSyncChatPrefix(out var commandPrefix, out var label, out var transportError))
        {
            this.status = transportError;
            return;
        }

        var code = SyncCode.Encode(payload);
        if (!this.SendSnowSyncCode(commandPrefix, code))
        {
            return;
        }

        this.generatedCode = code;
        this.decodedSummary = FormatSummary(payload);
        this.lastSnowSyncBroadcastUtc = DateTime.UtcNow;
        this.lastOutboundSnowSyncCode = code;
        this.lastOutboundSnowSyncUtc = DateTime.UtcNow;
        if (reason is not "heartbeat")
        {
            this.status = $"Broadcast {reason} video sync through {label}.";
        }

        if (reason is "host")
        {
            this.ScheduleSnowSyncBurst(commandPrefix, code);
        }
    }

    private bool SendSnowSyncCode(string commandPrefix, string code)
    {
        // VS2 codes are self-identifying, so live syncs are just the bare code.
        return this.TrySendChatCommand($"{commandPrefix} {code}");
    }

    private bool TrySendChatCommand(string command)
    {
        try
        {
            // Snowcloak's /ss commands are Dalamud commands; game chat commands
            // like /cwlinkshell go through the real chat box entry point.
            if (this.syncTransportIndex == SyncTransportSnowcloak &&
                Plugin.CommandManager.ProcessCommand(command))
            {
                return true;
            }

            if (!GameChat.TrySendMessage(command, out var error))
            {
                this.status = $"Could not send the chat message: {error}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not send video sync chat command.");
            this.status = "Could not send the chat message. Check the selected channel and try Test channel.";
            return false;
        }
    }

    private void ScheduleSnowSyncBurst(string commandPrefix, string code)
    {
        var now = DateTime.UtcNow;
        this.pendingSnowSyncBursts.Add(new SnowSyncBurst(now.AddMilliseconds(1500), commandPrefix, code));
    }

    private void TickSnowSyncBroadcast()
    {
        var now = DateTime.UtcNow;
        for (var i = this.pendingSnowSyncBursts.Count - 1; i >= 0; i--)
        {
            var burst = this.pendingSnowSyncBursts[i];
            if (now < burst.DueUtc)
            {
                continue;
            }

            this.SendSnowSyncCode(burst.CommandPrefix, burst.Code);
            this.lastOutboundSnowSyncCode = burst.Code;
            this.lastOutboundSnowSyncUtc = now;
            this.pendingSnowSyncBursts.RemoveAt(i);
        }

        if (!this.hostSnowSync)
        {
            this.pendingSnowSyncBroadcastUtc = DateTime.MinValue;
            return;
        }

        if (this.rendererProcess is null || this.rendererProcess.HasExited)
        {
            return;
        }

        if (this.pendingSnowSyncBroadcastUtc != DateTime.MinValue && now >= this.pendingSnowSyncBroadcastUtc)
        {
            this.pendingSnowSyncBroadcastUtc = DateTime.MinValue;
            this.BroadcastCurrentSnowSync("updated");
            return;
        }

        if (now - this.lastSnowSyncBroadcastUtc >= TimeSpan.FromSeconds(SnowHeartbeatSeconds))
        {
            this.BroadcastCurrentSnowSync("heartbeat");
        }
    }

    private void QueueSnowSyncBroadcast()
    {
        if (!this.hostSnowSync)
        {
            return;
        }

        this.pendingSnowSyncBroadcastUtc = DateTime.UtcNow.AddSeconds(SnowUpdateDebounceSeconds);
    }

    private bool TryCreateCurrentSyncPayload(out SyncPayload payload, out string error)
    {
        payload = default;
        error = string.Empty;

        var running = this.rendererProcess is not null && !this.rendererProcess.HasExited;
        if (!running && TryDecode(this.generatedCode, out payload, out _))
        {
            payload = payload with
            {
                Screen = this.CreateCurrentSyncScreen(),
                Audio = this.CreateCurrentSyncAudio(),
                Options = this.CreateCurrentSyncOptions(),
            };
            return true;
        }

        this.TryUpdatePlaybackStatus();

        var videoId = this.currentVideoId;
        if (string.IsNullOrWhiteSpace(videoId) && !TryExtractYouTubeId(this.youtubeUrl, out videoId))
        {
            error = "Snow sync needs a YouTube video. Start one in-world or enter a YouTube URL first.";
            return false;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        payload = new SyncPayload(
            videoId,
            now,
            Math.Round(Math.Max(0, this.GetEstimatedPlaybackTime()), 2),
            this.playbackPaused ? 0.0 : 1.0,
            this.CreateCurrentSyncScreen(),
            this.CreateCurrentSyncAudio(),
            this.CreateCurrentSyncOptions());
        return true;
    }

    private SyncScreen? CreateCurrentSyncScreen()
    {
        if (this.worldScreenAnchor is not { } anchor)
        {
            return null;
        }

        // Rounded so encoded codes stay well under the game's 500-byte chat limit.
        return new SyncScreen(
            this.worldScreenEnabled,
            MathF.Round(anchor.X, 2),
            MathF.Round(anchor.Y, 2),
            MathF.Round(anchor.Z, 2),
            MathF.Round(this.worldScreenRotation, 3),
            MathF.Round(this.worldScreenWidth, 2),
            MathF.Round(this.worldScreenDistance, 2),
            MathF.Round(this.worldScreenHeightOffset, 2),
            this.worldScreenActorOcclusion,
            MathF.Round(this.worldScreenOcclusionPadding, 1));
    }

    // Snapshot every part of the live screen placement so a room join code can
    // carry the host's exact layout: world position, rotation, size, stretch,
    // aspect lock, the elevation/push accumulators, and occlusion.
    private RoomScreenLayout? CaptureCurrentScreenLayout()
    {
        if (this.worldScreenAnchor is not { } anchor)
        {
            return null;
        }

        return new RoomScreenLayout(
            this.worldScreenEnabled,
            anchor.X,
            anchor.Y,
            anchor.Z,
            this.worldScreenRotation,
            this.worldScreenWidth,
            this.worldScreenHeight,
            this.worldScreenLockAspect,
            this.worldScreenElevation,
            this.worldScreenPush,
            this.worldScreenDistance,
            this.worldScreenHeightOffset,
            this.worldScreenActorOcclusion,
            this.worldScreenOcclusionPadding);
    }

    // Drop the host's captured screen layout straight onto this client's fields so
    // the joiner's TV matches the host's placement exactly. Callers enable the
    // screen afterwards; DrawWorldSurfaceOverlay re-derives height from width when
    // the aspect is locked, so a locked host stays locked here too.
    private void ApplyRoomScreenLayout(RoomScreenLayout layout)
    {
        this.worldScreenAnchor = new Vector3(layout.X, layout.Y, layout.Z);
        this.worldScreenRotation = layout.Rotation;
        this.worldScreenWidth = layout.Width;
        this.worldScreenHeight = layout.Height;
        this.worldScreenLockAspect = layout.LockAspect;
        this.worldScreenElevation = layout.Elevation;
        this.worldScreenPush = layout.Push;
        this.worldScreenDistance = layout.Distance;
        this.worldScreenHeightOffset = layout.HeightOffset;
        this.worldScreenActorOcclusion = layout.ActorOcclusion;
        this.worldScreenOcclusionPadding = layout.OcclusionPadding;
    }

    // Build the shareable room code from the live screen layout, so whatever the
    // host has on screen right now is what a joiner reproduces.
    private string BuildRoomShareCode(Watch2GetherRoom room)
    {
        return Watch2GetherRoomCode.Encode(room with { Layout = this.CaptureCurrentScreenLayout() });
    }

    private SyncAudio CreateCurrentSyncAudio()
    {
        return new SyncAudio(
            MathF.Round(this.masterVolume, 2),
            this.audioMuted,
            this.spatialAudio,
            MathF.Round(this.audioRange, 1));
    }

    private SyncOptions CreateCurrentSyncOptions()
    {
        return new SyncOptions(
            this.videoFullscreen,
            this.adBlockEnabled,
            !this.browserShown);
    }

    private void ApplyRemoteSync(SyncPayload payload, string source)
    {
        var syncVersion = ++this.remoteSyncVersion;
        var running = this.rendererProcess is not null && !this.rendererProcess.HasExited;
        var targetSeconds = Math.Max(0, payload.GetCurrentVideoSeconds());

        if (running && string.Equals(this.currentVideoId, payload.VideoId, StringComparison.Ordinal))
        {
            // Already in this party: one gentle correction, and only touch the
            // presentation when something actually changed. Re-applying identical
            // state every heartbeat is what caused the periodic playback jitter.
            this.ApplyRemotePresentation(payload);
            if (this.ApplyRemotePlaybackCorrection(payload, RunningSyncDriftThresholdSeconds))
            {
                this.status = $"Applied {source} sync correction at {FormatTimestamp(targetSeconds)}.";
            }

            return;
        }

        this.StartInWindowPreview(payload);
        this.ApplyRemotePresentation(payload);
        this.SchedulePlaybackCorrections(payload, syncVersion);
        this.status = $"Joined {source} video sync at {FormatTimestamp(targetSeconds)}.";
    }

    private void ApplyRemotePresentation(SyncPayload payload)
    {
        // Hosts re-broadcast the full state every heartbeat, so skip anything that
        // matches what we already have; re-applying options or the screen causes
        // visible hitches in the player.
        if (payload.Options is { } options)
        {
            var optionsChanged = this.adBlockEnabled != options.AdBlock
                || this.browserShown != !options.HideBrowser
                || this.videoFullscreen != options.VideoFullscreen;
            this.adBlockEnabled = options.AdBlock;
            this.browserShown = !options.HideBrowser;
            this.videoFullscreen = options.VideoFullscreen;
            if (optionsChanged)
            {
                var flags = (options.AdBlock ? 1 : 0) |
                            (options.VideoFullscreen ? 2 : 0) |
                            (options.HideBrowser ? 4 : 0);
                this.SendPlaybackCommand("applyoptions", flags);
            }
        }

        if (payload.Audio is { } audio)
        {
            var newVolume = Math.Clamp(audio.Volume, 0f, 1f);
            var newRange = Math.Clamp(audio.Range, 5f, 100f);
            var audioChanged = Math.Abs(this.masterVolume - newVolume) > 0.005f
                || this.audioMuted != audio.Muted
                || this.spatialAudio != audio.Spatial
                || Math.Abs(this.audioRange - newRange) > 0.5f;
            this.masterVolume = newVolume;
            this.audioMuted = audio.Muted;
            this.spatialAudio = audio.Spatial;
            this.audioRange = newRange;
            if (audioChanged)
            {
                this.lastSentVolume = -1f;
                this.lastAudioWriteUtc = DateTime.MinValue;
            }
        }

        if (payload.Screen is not { } screen)
        {
            return;
        }

        var newAnchor = new Vector3(screen.X, screen.Y, screen.Z);
        var newWidth = Math.Clamp(screen.Width, 1f, 10f);
        var newDistance = Math.Clamp(screen.Distance, 1f, 12f);
        var newHeight = Math.Clamp(screen.HeightOffset, 0.2f, 4f);
        var newPadding = Math.Clamp(screen.OcclusionPadding, 0f, 48f);
        var screenChanged = this.worldScreenAnchor != newAnchor
            || this.worldScreenRotation != screen.Rotation
            || this.worldScreenWidth != newWidth
            || this.worldScreenDistance != newDistance
            || this.worldScreenHeightOffset != newHeight
            || this.worldScreenActorOcclusion != screen.ActorOcclusion
            || this.worldScreenOcclusionPadding != newPadding
            || this.worldScreenEnabled != screen.Enabled;
        if (!screenChanged)
        {
            return;
        }

        this.worldScreenAnchor = newAnchor;
        this.worldScreenRotation = screen.Rotation;
        this.worldScreenWidth = newWidth;
        this.worldScreenDistance = newDistance;
        this.worldScreenHeightOffset = newHeight;
        this.worldScreenActorOcclusion = screen.ActorOcclusion;
        this.worldScreenOcclusionPadding = newPadding;
        this.SetWorldScreenEnabledFromSync(screen.Enabled);
    }

    private void SetWorldScreenEnabledFromSync(bool enabled)
    {
        this.worldScreenEnabled = enabled;
        if (!enabled)
        {
            this.presentHookProbe.NativeTestDrawEnabled = false;
            this.presentHookProbe.NativeScreenSpaceProbeEnabled = false;
            this.presentHookProbe.ClearNativeQuad();
            return;
        }

        this.drawImguiWorldScreen = false;
        if (this.presentHookProbe.TryInstall())
        {
            this.presentHookProbe.NativeTestDrawEnabled = true;
            this.presentHookProbe.NativeScreenSpaceProbeEnabled = false;
            return;
        }

        this.presentHookProbe.NativeTestDrawEnabled = false;
        this.presentHookProbe.NativeScreenSpaceProbeEnabled = false;
        this.drawImguiWorldScreen = true;
        this.worldScreenActorOcclusion = true;
    }

    private bool ApplyRemotePlaybackCorrection(SyncPayload payload, double thresholdSeconds)
    {
        var targetSeconds = Math.Max(0, payload.GetCurrentVideoSeconds());
        var paused = payload.PlaybackRate == 0;
        this.TryUpdatePlaybackStatus();
        var drift = Math.Abs(this.GetEstimatedPlaybackTime() - targetSeconds);
        if (this.playbackPaused != paused)
        {
            this.SendPlaybackCommand(paused ? "syncpause" : "syncplay", targetSeconds);
            return true;
        }

        if (paused)
        {
            if (drift > SoftSyncDriftThresholdSeconds)
            {
                this.SendPlaybackCommand("syncpause", targetSeconds);
                return true;
            }

            return false;
        }

        if (drift > thresholdSeconds)
        {
            this.SendPlaybackCommand("syncplay", targetSeconds);
            return true;
        }

        if (drift > SoftSyncDriftThresholdSeconds)
        {
            this.SendPlaybackCommand("syncsoftplay", targetSeconds);
            return true;
        }

        return false;
    }

    private void SchedulePlaybackCorrections(SyncPayload payload, int syncVersion)
    {
        _ = Task.Run(async () =>
        {
            foreach (var delay in new[] { 180, 700, 1800, 3500 })
            {
                await Task.Delay(delay).ConfigureAwait(false);
                if (syncVersion != this.remoteSyncVersion)
                {
                    return;
                }

                this.ApplyRemotePlaybackCorrection(payload, StartupSyncDriftThresholdSeconds);
            }
        });
    }

    private void OpenInDefaultBrowser(SyncPayload payload)
    {
        Process.Start(new ProcessStartInfo(BuildWatchUrl(payload)) { UseShellExecute = true });
    }

    private void OpenInOverlay(SyncPayload payload)
    {
        var overlayPath = Path.Combine(this.pluginDirectory, "OverlayPlayer", "OverlayPlayer.exe");
        if (!File.Exists(overlayPath))
        {
            this.status = $"Overlay player was not found at {overlayPath}";
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = overlayPath,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(overlayPath) ?? AppContext.BaseDirectory,
            ArgumentList = { BuildWatchUrl(payload) },
        });

        this.status = "Opened the overlay player with the normal YouTube watch page.";
    }

    private void PlayUrlInWorld(string input)
    {
        string url;
        if (TryExtractYouTubeId(input, out var videoId))
        {
            url = $"https://www.youtube.com/watch?v={Uri.EscapeDataString(videoId)}&autoplay=1";
            this.currentVideoId = videoId;
        }
        else if (Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            url = uri.ToString();
            this.currentVideoId = string.Empty;
        }
        else
        {
            this.status = "Enter a YouTube (or any http/https) video URL first.";
            return;
        }

        if (!this.StartRendererBridge(url))
        {
            return;
        }

        this.playingWatch2GetherRoom = false;
        if (!this.PlaceWorldScreenInFrontOfPlayer())
        {
            return;
        }

        this.EnableNativeWorldScreen();
        this.status = "Playing on the in-world screen. Use the playback controls on the Screen tab.";
    }

    private void OpenFreshBrowserOnScreen(string? input = null)
    {
        var candidate = string.IsNullOrWhiteSpace(input)
            ? "https://www.google.com/"
            : input.Trim();
        var url = Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.ToString()
            : "https://www.google.com/";

        if (!this.StartRendererBridge(url))
        {
            return;
        }

        this.currentVideoId = string.Empty;
        this.playingWatch2GetherRoom = false;
        if (!this.PlaceWorldScreenInFrontOfPlayer())
        {
            return;
        }

        this.EnableNativeWorldScreen();
        this.AllowRendererForeground();
        this.SendPlaybackCommand("show");
        this.status = this.networkSync.State == NetworkSync.SyncState.Hosting
            ? "Opened a fresh browser on the TV. Your shell will sync navigation and page position."
            : "Opened a fresh browser on the in-world TV.";
    }

    private void StopPlayback()
    {
        this.StopInWindowPreview();
        this.currentVideoId = string.Empty;
        this.playingWatch2GetherRoom = false;
        this.worldScreenEnabled = false;
        this.browserShown = false;
        this.videoFullscreen = false;
        this.presentHookProbe.NativeTestDrawEnabled = false;
        this.presentHookProbe.ClearNativeQuad();
        this.status = "Stopped playback and hid the world screen.";
    }

    private void StartInWindowPreview(SyncPayload payload)
    {
        if (!this.StartRendererBridge(BuildWatchUrl(payload)))
        {
            return;
        }

        this.currentVideoId = payload.VideoId;
        if (!this.PlaceWorldScreenInFrontOfPlayer())
        {
            return;
        }

        this.EnableNativeWorldScreen();
        this.status = "Started the synced video on the in-world screen.";
    }

    private bool StartRendererBridge(string url, string? watch2GetherShareUrl = null)
    {
        var overlayPath = Path.Combine(this.pluginDirectory, "OverlayPlayer", "OverlayPlayer.exe");
        if (!File.Exists(overlayPath))
        {
            this.status = $"Overlay player was not found at {overlayPath}";
            return false;
        }

        this.StopRendererProcess();
        this.lastRendererUrl = url;
        this.lastRendererShareUrl = watch2GetherShareUrl;
        this.frameTexture?.Dispose();
        this.frameTexture = null;
        this.frameTextureTask = null;
        this.lastLoadedFrameWriteUtc = DateTime.MinValue;
        this.browserShown = false;
        this.lastCaptureFrameCount = 0;
        this.lastCaptureFpsSampleUtc = DateTime.MinValue;
        this.captureFps = 0f;

        var framePath = this.GetCaptureFramePath();
        TryDeleteFile(framePath);
        TryDeleteFile(Path.Combine(this.pluginDirectory, "videosync-preview.png"));
        TryDeleteFile(this.GetSharedInfoPath());
        TryDeleteFile(this.GetSharedInfoPath() + ".tmp");
        TryDeleteFile(this.GetSharedInfoPath() + ".error.txt");
        TryDeleteFile(this.GetControlPath());
        TryDeleteFile(this.GetControlPath() + ".tmp");
        TryDeleteFile(this.GetStatusPath());
        TryDeleteFile(this.GetStatusPath() + ".tmp");
        TryDeleteFile(this.GetAudioPath());
        TryDeleteFile(this.GetAudioPath() + ".tmp");
        this.sharedTextureHandle = 0;
        this.lastSharedInfoWriteUtc = DateTime.MinValue;
        this.lastStatusReadUtc = DateTime.MinValue;
        this.playbackTime = 0;
        this.playbackDuration = 0;
        this.playbackPaused = true;
        this.lastSentVolume = -1f;
        this.lastAudioWriteUtc = DateTime.MinValue;
        this.videoFullscreen = false;

        var startInfo = new ProcessStartInfo
        {
            FileName = overlayPath,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(overlayPath) ?? AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add("--capture");
        startInfo.ArgumentList.Add(url);
        startInfo.ArgumentList.Add(framePath);
        startInfo.ArgumentList.Add("--adblock");
        startInfo.ArgumentList.Add(this.adBlockEnabled ? "enabled" : "disabled");
        var (captureWidth, captureHeight) = this.ResolveCaptureSize();
        startInfo.ArgumentList.Add("--capture-size");
        startInfo.ArgumentList.Add($"{captureWidth}x{captureHeight}");
        startInfo.ArgumentList.Add("--capture-mode");
        startInfo.ArgumentList.Add(this.captureMode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--foreground-capture");
        startInfo.ArgumentList.Add(this.foregroundCapture ? "enabled" : "disabled");
        if (!string.IsNullOrWhiteSpace(watch2GetherShareUrl))
        {
            startInfo.ArgumentList.Add("--w2g-share");
            startInfo.ArgumentList.Add(watch2GetherShareUrl);
        }

        if (this.presentHookProbe.TryGetGameAdapterLuid(out var adapterLuid, out var adapterName))
        {
            startInfo.ArgumentList.Add("--adapter-luid");
            startInfo.ArgumentList.Add(adapterLuid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            this.status = $"Starting renderer bridge on game GPU: {adapterName}.";
        }

        this.rendererProcess = Process.Start(startInfo);
        this.rendererStartedUtc = DateTime.UtcNow;

        return true;
    }

}
