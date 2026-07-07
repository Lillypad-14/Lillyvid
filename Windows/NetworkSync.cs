using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VideoSyncPrototype.Windows;

/// <summary>
/// Client for the Nearby Broadcast relay (server/index.js): lightweight watch rooms that
/// synchronize page state (URL + scroll position) from a host to viewers.
///
/// Thread model: a background task owns the ClientWebSocket receive loop and parses frames
/// into <see cref="Event"/>s on a queue; the UI thread drains them in <see cref="Tick"/>,
/// which also drives reconnects, heartbeats, throttled host broadcasts, and staleness
/// checks. All public members are meant to be called from the UI thread only.
///
/// Privacy: the only fields that ever leave this client are the display name, an optional
/// room name, and the host's current URL + scroll offsets + timestamp.
/// </summary>
public sealed class NetworkSync : IDisposable
{
    public enum SyncState
    {
        Disabled,
        Connecting,
        Connected,   // in the lobby: can host, join, or browse the shell list
        Hosting,
        Viewing,
        Reconnecting,
    }

    public readonly record struct ShellInfo(string Room, string Name, string Host, int Users, int AgeSec, bool Stale);

    public readonly record struct PageState(
        string Url,
        int ScrollX,
        int ScrollY,
        int ViewportWidth,
        int ViewportHeight,
        int DocumentWidth,
        int DocumentHeight,
        double Zoom,
        double MediaTime,
        double MediaDuration,
        bool MediaPaused,
        double MediaRate,
        bool MediaMuted,
        bool MediaFullscreen,
        RoomScreenLayout? Layout,
        long Timestamp)
    {
        public bool HasMedia => this.MediaDuration > 0.5 || this.MediaTime > 0.05;
    }

    // ---- Tunables ----------------------------------------------------------------------
    private const double UrlSendMinGapSeconds = 0.25;
    private const double ScrollSendMinGapSeconds = 0.4;
    private const int ScrollSendThresholdPx = 48;
    private const double HostHeartbeatSeconds = 10;
    private const double ViewerKeepaliveSeconds = 20;
    private const double HostStaleAfterSeconds = 45;
    private const double RoomListRefreshSeconds = 5;
    private static readonly int[] ReconnectDelaysSeconds = [1, 2, 5, 10, 15];
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
    };

    private sealed record Event(string Type, JsonDocument? Doc)
    {
        public static Event Parse(string json) => new("message", JsonDocument.Parse(json));
        public static readonly Event Disconnected = new("disconnected", null);
    }

    private readonly ConcurrentQueue<Event> events = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly List<ShellInfo> rooms = [];

    private ClientWebSocket? socket;
    private CancellationTokenSource? lifetime;
    private Task? receiveTask;
    private int connectionEpoch;

    private string relayUrl = string.Empty;
    private string displayName = "Viewer";
    private bool roomUrlRelay;

    // Re-join intent, preserved across reconnects.
    private string? currentRoom;
    private string? hostToken;      // set while hosting; enables resume after reconnect
    private bool wantHost;

    private DateTime nextReconnectUtc = DateTime.MinValue;
    private int reconnectAttempt;
    private DateTime lastListRequestUtc = DateTime.MinValue;
    private DateTime lastKeepaliveUtc = DateTime.MinValue;
    private DateTime lastHostSignalUtc = DateTime.MinValue;

    // Host-side throttle bookkeeping.
    private string lastSentUrl = string.Empty;
    private int lastSentScrollX = -1;
    private int lastSentScrollY = -1;
    private int lastSentViewportWidth = -1;
    private int lastSentViewportHeight = -1;
    private int lastSentDocumentWidth = -1;
    private int lastSentDocumentHeight = -1;
    private double lastSentZoom = -1;
    private double lastSentMediaTime = -1;
    private bool lastSentMediaPaused = true;
    private double lastSentMediaRate = 1;
    private bool lastSentMediaMuted;
    private bool lastSentMediaFullscreen;
    private RoomScreenLayout? lastSentLayout;
    private DateTime lastStateSentUtc = DateTime.MinValue;

    // Viewer-side: latest state waiting to be applied by the UI layer.
    private PageState? pendingApply;

    public SyncState State { get; private set; } = SyncState.Disabled;

    public string StatusText { get; private set; } = "Sync is off.";

    public string RoomId => this.currentRoom ?? string.Empty;

    public string RoomName { get; private set; } = string.Empty;

    public string HostName { get; private set; } = string.Empty;

    public int UserCount { get; private set; }

    public bool HostPresent { get; private set; } = true;

    public bool UsesRoomCodeRelay => this.roomUrlRelay;

    public bool HostStale =>
        this.State == SyncState.Viewing &&
        (!this.HostPresent || (DateTime.UtcNow - this.lastHostSignalUtc).TotalSeconds > HostStaleAfterSeconds);

    public IReadOnlyList<ShellInfo> Rooms => this.rooms;

    // ---- Lifecycle -----------------------------------------------------------------------

    public void Enable(string url, string name)
    {
        if (this.State != SyncState.Disabled)
        {
            return;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss"))
        {
            this.StatusText = "Set a valid relay URL (wss://…) in the field above first.";
            return;
        }

        this.relayUrl = uri.ToString();
        this.roomUrlRelay = LooksLikeRoomUrlRelay(uri);
        this.displayName = string.IsNullOrWhiteSpace(name) ? "Viewer" : name.Trim();
        this.reconnectAttempt = 0;
        this.StartConnect();
    }

    public void Disable()
    {
        this.TeardownSocket();
        this.currentRoom = null;
        this.hostToken = null;
        this.wantHost = false;
        this.roomUrlRelay = false;
        this.pendingApply = null;
        this.rooms.Clear();
        this.State = SyncState.Disabled;
        this.StatusText = "Sync is off.";
    }

    public void Dispose() => this.Disable();

    // ---- Lobby actions -------------------------------------------------------------------

    public void HostShell(string roomName)
    {
        if (this.State != SyncState.Connected)
        {
            return;
        }

        this.wantHost = true;
        this.RoomName = roomName;
        if (this.roomUrlRelay)
        {
            this.currentRoom = NewLocalRoomCode();
            this.hostToken = null;
            this.HostName = this.displayName;
            this.UserCount = 1;
            this.StatusText = "Creating your broadcast shell…";
            this.StartConnect();
            return;
        }

        this.Send(new { t = "create", roomName });
        this.StatusText = "Creating your broadcast shell…";
    }

    public void JoinShell(string roomId)
    {
        if (this.State != SyncState.Connected || string.IsNullOrWhiteSpace(roomId))
        {
            return;
        }

        this.wantHost = false;
        if (this.roomUrlRelay)
        {
            this.currentRoom = CleanRoomId(roomId);
            this.RoomName = this.currentRoom;
            this.HostName = string.Empty;
            this.UserCount = 1;
            this.StatusText = "Joining…";
            this.StartConnect();
            return;
        }

        this.Send(new { t = "join", room = CleanRoomId(roomId) });
        this.StatusText = "Joining…";
    }

    public void RefreshShellList()
    {
        if (this.State != SyncState.Connected)
        {
            return;
        }

        this.StatusText = "Searching for nearby shells…";
        this.lastListRequestUtc = DateTime.UtcNow;
        if (!this.roomUrlRelay)
        {
            this.Send(new { t = "list" });
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var json = await Http.GetStringAsync(this.BuildRoomsHttpUri()).ConfigureAwait(false);
                using var source = JsonDocument.Parse(json);
                using var mapped = JsonDocument.Parse(MapRoomsResponse(source.RootElement));
                this.events.Enqueue(new Event("message", JsonDocument.Parse(mapped.RootElement.GetRawText())));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or UriFormatException)
            {
                this.events.Enqueue(Event.Parse("{\"t\":\"roomSearchFailed\"}"));
            }
        });
    }

    public void LeaveShell()
    {
        if (this.State is not (SyncState.Hosting or SyncState.Viewing))
        {
            return;
        }

        if (!this.roomUrlRelay)
        {
            this.Send(new { t = "leave" });
        }

        this.currentRoom = null;
        this.hostToken = null;
        this.wantHost = false;
        this.pendingApply = null;
        this.State = SyncState.Connected;
        this.StatusText = "Left the shell.";
        if (this.roomUrlRelay)
        {
            this.StartConnect();
        }
    }

    // ---- Host: page-state reporting (throttled) -------------------------------------------

    /// <summary>
    /// Called every frame with the browser's current page state while hosting. Sends only
    /// meaningful changes: URL changes go out (almost) immediately, scroll moves must beat
    /// a pixel threshold and a minimum gap, and a heartbeat state goes out periodically so
    /// the relay and late joiners always have something fresh.
    /// </summary>
    public void ReportHostPageState(PageState state)
    {
        if (this.State != SyncState.Hosting || string.IsNullOrWhiteSpace(state.Url) ||
            !state.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var sinceLast = (now - this.lastStateSentUtc).TotalSeconds;
        var urlChanged = !string.Equals(state.Url, this.lastSentUrl, StringComparison.Ordinal);
        var scrollDelta = Math.Abs(state.ScrollX - this.lastSentScrollX) + Math.Abs(state.ScrollY - this.lastSentScrollY);
        var viewportChanged = Math.Abs(state.ViewportWidth - this.lastSentViewportWidth) >= 8 ||
                              Math.Abs(state.ViewportHeight - this.lastSentViewportHeight) >= 8 ||
                              Math.Abs(state.DocumentWidth - this.lastSentDocumentWidth) >= 16 ||
                              Math.Abs(state.DocumentHeight - this.lastSentDocumentHeight) >= 16 ||
                              Math.Abs(state.Zoom - this.lastSentZoom) >= 0.02;
        var mediaChanged = state.HasMedia &&
                           (Math.Abs(state.MediaTime - this.lastSentMediaTime) >= 1.0 ||
                            state.MediaPaused != this.lastSentMediaPaused ||
                            Math.Abs(state.MediaRate - this.lastSentMediaRate) >= 0.02 ||
                            state.MediaMuted != this.lastSentMediaMuted ||
                            state.MediaFullscreen != this.lastSentMediaFullscreen);
        var layoutChanged = state.Layout != this.lastSentLayout;
        var send = urlChanged
            ? sinceLast >= UrlSendMinGapSeconds
            : (scrollDelta >= ScrollSendThresholdPx && sinceLast >= ScrollSendMinGapSeconds)
              || (viewportChanged && sinceLast >= ScrollSendMinGapSeconds)
              || (mediaChanged && sinceLast >= ScrollSendMinGapSeconds)
              || (layoutChanged && sinceLast >= ScrollSendMinGapSeconds)
              || sinceLast >= HostHeartbeatSeconds;
        if (!send)
        {
            return;
        }

        this.lastSentUrl = state.Url;
        this.lastSentScrollX = state.ScrollX;
        this.lastSentScrollY = state.ScrollY;
        this.lastSentViewportWidth = state.ViewportWidth;
        this.lastSentViewportHeight = state.ViewportHeight;
        this.lastSentDocumentWidth = state.DocumentWidth;
        this.lastSentDocumentHeight = state.DocumentHeight;
        this.lastSentZoom = state.Zoom;
        this.lastSentMediaTime = state.MediaTime;
        this.lastSentMediaPaused = state.MediaPaused;
        this.lastSentMediaRate = state.MediaRate;
        this.lastSentMediaMuted = state.MediaMuted;
        this.lastSentMediaFullscreen = state.MediaFullscreen;
        this.lastSentLayout = state.Layout;
        this.lastStateSentUtc = now;
        if (this.roomUrlRelay)
        {
            this.Send(new
            {
                type = "pageSync",
                url = state.Url,
                scrollX = state.ScrollX,
                scrollY = state.ScrollY,
                viewportW = state.ViewportWidth,
                viewportH = state.ViewportHeight,
                documentW = state.DocumentWidth,
                documentH = state.DocumentHeight,
                zoom = state.Zoom,
                mediaTime = state.MediaTime,
                mediaDuration = state.MediaDuration,
                mediaPaused = state.MediaPaused,
                mediaRate = state.MediaRate,
                mediaMuted = state.MediaMuted,
                mediaFullscreen = state.MediaFullscreen,
                layout = state.Layout,
                ts = state.Timestamp,
            });
            return;
        }

        this.Send(new
        {
            t = "state",
            url = state.Url,
            sx = state.ScrollX,
            sy = state.ScrollY,
            vw = state.ViewportWidth,
            vh = state.ViewportHeight,
            dw = state.DocumentWidth,
            dh = state.DocumentHeight,
            z = state.Zoom,
            mt = state.MediaTime,
            md = state.MediaDuration,
            mp = state.MediaPaused,
            mr = state.MediaRate,
            mm = state.MediaMuted,
            mf = state.MediaFullscreen,
            layout = state.Layout,
            ts = state.Timestamp,
        });
    }

    // ---- Viewer: state application ---------------------------------------------------------

    /// <summary>Takes the latest host state waiting to be applied, if any.</summary>
    public bool TryTakePendingApply(out PageState state)
    {
        if (this.pendingApply is { } pending)
        {
            this.pendingApply = null;
            state = pending;
            return true;
        }

        state = default;
        return false;
    }

    // ---- Per-frame pump ---------------------------------------------------------------------

    public void Tick()
    {
        if (this.State == SyncState.Disabled)
        {
            return;
        }

        this.DrainEvents();

        var now = DateTime.UtcNow;
        switch (this.State)
        {
            case SyncState.Reconnecting when now >= this.nextReconnectUtc:
                this.StartConnect();
                break;

            case SyncState.Connected when (now - this.lastListRequestUtc).TotalSeconds >= RoomListRefreshSeconds:
                this.lastListRequestUtc = now;
                if (!this.roomUrlRelay)
                {
                    this.Send(new { t = "list" });
                }

                break;

            case SyncState.Hosting when (now - this.lastStateSentUtc).TotalSeconds >= HostHeartbeatSeconds
                                        && (now - this.lastKeepaliveUtc).TotalSeconds >= HostHeartbeatSeconds:
                // No page state flowing (e.g. browser closed): plain ping keeps the shell alive.
                this.lastKeepaliveUtc = now;
                if (!this.roomUrlRelay)
                {
                    this.Send(new { t = "ping" });
                }

                break;

            case SyncState.Viewing when (now - this.lastKeepaliveUtc).TotalSeconds >= ViewerKeepaliveSeconds:
                this.lastKeepaliveUtc = now;
                if (!this.roomUrlRelay)
                {
                    this.Send(new { t = "ping" });
                }

                break;
        }
    }

    // ---- Internals ---------------------------------------------------------------------------

    private void StartConnect()
    {
        this.TeardownSocket();
        this.State = SyncState.Connecting;
        this.StatusText = this.reconnectAttempt > 0
            ? $"Reconnecting (attempt {this.reconnectAttempt})…"
            : "Connecting to the relay…";

        var epoch = ++this.connectionEpoch;
        var cts = new CancellationTokenSource();
        this.lifetime = cts;
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        this.socket = ws;

        this.receiveTask = Task.Run(async () =>
        {
            try
            {
                await ws.ConnectAsync(this.BuildConnectUri(), cts.Token).ConfigureAwait(false);
                this.events.Enqueue(new Event("connected", null));
                var buffer = new byte[16 * 1024];
                var text = new StringBuilder();
                while (ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(buffer, cts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (!result.EndOfMessage)
                    {
                        continue;
                    }

                    try
                    {
                        this.events.Enqueue(Event.Parse(text.ToString()));
                    }
                    catch (JsonException)
                    {
                        // Malformed frame; ignore.
                    }

                    text.Clear();
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
            {
                // Fall through to the disconnect event.
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Nearby sync receive loop failed.");
            }

            if (epoch == this.connectionEpoch)
            {
                this.events.Enqueue(Event.Disconnected);
            }
        });
    }

    private void TeardownSocket()
    {
        this.connectionEpoch++;
        try
        {
            this.lifetime?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        this.socket?.Dispose();
        this.socket = null;
        this.lifetime = null;
        this.receiveTask = null;
        while (this.events.TryDequeue(out var stale))
        {
            stale.Doc?.Dispose();
        }
    }

    private void DrainEvents()
    {
        while (this.events.TryDequeue(out var evt))
        {
            switch (evt.Type)
            {
                case "connected":
                    this.OnConnected();
                    break;
                case "disconnected":
                    this.OnDisconnected();
                    break;
                case "message":
                    using (evt.Doc)
                    {
                        if (evt.Doc is not null)
                        {
                            this.OnMessage(evt.Doc.RootElement);
                        }
                    }

                    break;
            }
        }
    }

    private void OnConnected()
    {
        this.reconnectAttempt = 0;
        if (this.roomUrlRelay)
        {
            this.State = this.currentRoom is null ? SyncState.Connected : SyncState.Connecting;
            this.StatusText = this.currentRoom is null
                ? "Connected. Host a broadcast or join one below."
                : "Opening broadcast shell…";
            return;
        }

        this.Send(new { t = "hello", name = this.displayName });

        if (this.currentRoom is not null && this.wantHost && this.hostToken is not null)
        {
            // We were hosting: reclaim the shell.
            this.Send(new { t = "resume", room = this.currentRoom, token = this.hostToken });
            this.StatusText = "Reconnected — reclaiming your shell…";
            this.State = SyncState.Connecting;
            return;
        }

        if (this.currentRoom is not null && !this.wantHost)
        {
            this.Send(new { t = "join", room = this.currentRoom });
            this.StatusText = "Reconnected — rejoining…";
            this.State = SyncState.Connecting;
            return;
        }

        this.State = SyncState.Connected;
        this.StatusText = "Connected. Host a broadcast or join one below.";
        this.lastListRequestUtc = DateTime.MinValue; // refresh the lobby list immediately
    }

    private void OnDisconnected()
    {
        if (this.State == SyncState.Disabled)
        {
            return;
        }

        var delay = ReconnectDelaysSeconds[Math.Min(this.reconnectAttempt, ReconnectDelaysSeconds.Length - 1)];
        this.reconnectAttempt++;
        this.nextReconnectUtc = DateTime.UtcNow.AddSeconds(delay);
        this.State = SyncState.Reconnecting;
        this.StatusText = $"Connection lost. Retrying in {delay}s…";
    }

    private void OnMessage(JsonElement msg)
    {
        var type = msg.TryGetProperty("t", out var typeElement)
            ? typeElement.GetString()
            : msg.TryGetProperty("type", out var legacyTypeElement)
                ? legacyTypeElement.GetString()
                : null;
        switch (type)
        {
            case "welcome":
                this.OnRoomUrlWelcome(msg);
                break;

            case "hostGranted":
                this.OnRoomUrlHostGranted();
                break;

            case "hostChanged":
                if (this.State == SyncState.Hosting && !this.wantHost)
                {
                    this.State = SyncState.Viewing;
                    this.HostPresent = true;
                    this.StatusText = $"Following \"{this.RoomName}\".";
                }

                break;

            case "created":
                this.currentRoom = msg.GetProperty("room").GetString();
                this.hostToken = msg.GetProperty("token").GetString();
                this.RoomName = msg.TryGetProperty("roomName", out var rn) ? rn.GetString() ?? string.Empty : string.Empty;
                this.HostName = this.displayName;
                this.UserCount = 1;
                this.wantHost = true;
                this.State = SyncState.Hosting;
                this.StatusText = $"Hosting shell {this.currentRoom}.";
                this.lastSentUrl = string.Empty; // force a fresh state broadcast
                break;

            case "resumed":
                this.State = SyncState.Hosting;
                this.StatusText = "Shell reclaimed — you are hosting again.";
                this.lastSentUrl = string.Empty;
                break;

            case "joined":
                this.currentRoom = msg.GetProperty("room").GetString();
                this.RoomName = msg.TryGetProperty("roomName", out var joinedName) ? joinedName.GetString() ?? string.Empty : string.Empty;
                this.HostName = msg.TryGetProperty("host", out var hostElement) ? hostElement.GetString() ?? string.Empty : string.Empty;
                this.UserCount = msg.TryGetProperty("users", out var usersElement) ? usersElement.GetInt32() : 1;
                this.HostPresent = !msg.TryGetProperty("hostPresent", out var hp) || hp.GetBoolean();
                this.lastHostSignalUtc = DateTime.UtcNow;
                this.State = SyncState.Viewing;
                this.StatusText = $"Following {this.HostName} in \"{this.RoomName}\".";
                if (msg.TryGetProperty("state", out var snapshot) && snapshot.ValueKind == JsonValueKind.Object)
                {
                    this.QueueApply(snapshot);
                }

                break;

            case "state":
            case "pageSync":
                if (this.State == SyncState.Viewing)
                {
                    this.lastHostSignalUtc = DateTime.UtcNow;
                    this.HostPresent = true;
                    this.QueueApply(msg);
                }

                break;

            case "peers":
                this.UserCount = msg.TryGetProperty("users", out var peersElement) ? peersElement.GetInt32() : this.UserCount;
                break;

            case "hostleft":
                this.HostPresent = false;
                this.StatusText = "The host disconnected. Waiting to see if they come back…";
                break;

            case "hostback":
                this.HostPresent = true;
                this.lastHostSignalUtc = DateTime.UtcNow;
                this.StatusText = $"Following {this.HostName} in \"{this.RoomName}\".";
                break;

            case "closed":
                var reason = msg.TryGetProperty("reason", out var reasonElement) ? reasonElement.GetString() : null;
                this.currentRoom = null;
                this.hostToken = null;
                this.wantHost = false;
                this.pendingApply = null;
                this.State = SyncState.Connected;
                this.StatusText = reason == "host_gone"
                    ? "The shell closed because the host never came back."
                    : "The shell expired.";
                break;

            case "rooms":
                this.rooms.Clear();
                if (msg.TryGetProperty("rooms", out var roomsElement) && roomsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var room in roomsElement.EnumerateArray())
                    {
                        this.rooms.Add(new ShellInfo(
                            room.GetProperty("room").GetString() ?? string.Empty,
                            room.TryGetProperty("roomName", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                            room.TryGetProperty("host", out var host) ? host.GetString() ?? string.Empty : string.Empty,
                            room.TryGetProperty("users", out var users) ? users.GetInt32() : 0,
                            room.TryGetProperty("ageSec", out var age) ? age.GetInt32() : 0,
                            room.TryGetProperty("stale", out var stale) && stale.GetBoolean()));
                    }
                }

                break;

            case "error":
                var code = msg.TryGetProperty("code", out var codeElement) ? codeElement.GetString() : "unknown";
                var text = msg.TryGetProperty("msg", out var msgElement) ? msgElement.GetString() : null;
                this.OnServerError(code ?? "unknown", text);
                break;

            case "pong":
                break;

            case "roomSearchFailed":
                this.rooms.Clear();
                this.StatusText = "Could not search shells. Make sure the relay has /api/rooms deployed.";
                break;
        }
    }

    private void OnRoomUrlWelcome(JsonElement msg)
    {
        this.roomUrlRelay = true;
        var requestedRoom = !string.IsNullOrWhiteSpace(this.currentRoom);
        var serverRoom = msg.TryGetProperty("room", out var roomElement)
            ? CleanRoomId(roomElement.GetString() ?? string.Empty)
            : string.Empty;
        if (requestedRoom && !string.IsNullOrWhiteSpace(serverRoom))
        {
            this.currentRoom = serverRoom;
        }

        this.RoomName = string.IsNullOrWhiteSpace(this.RoomName) ? this.currentRoom ?? string.Empty : this.RoomName;
        this.UserCount = msg.TryGetProperty("clients", out var clientsElement) && clientsElement.TryGetInt32(out var clients)
            ? clients
            : Math.Max(1, this.UserCount);
        this.HostPresent = true;
        this.lastHostSignalUtc = DateTime.UtcNow;

        var isHost = msg.TryGetProperty("isHost", out var hostElement) && hostElement.GetBoolean();
        if (!requestedRoom)
        {
            this.State = SyncState.Connected;
            this.StatusText = "Connected. Host a broadcast or join one below.";
            return;
        }

        if (isHost)
        {
            this.wantHost = true;
            this.HostName = this.displayName;
            this.State = SyncState.Hosting;
            this.StatusText = $"Hosting shell {this.currentRoom}.";
            this.lastSentUrl = string.Empty;
            return;
        }

        this.wantHost = false;
        this.HostName = string.IsNullOrWhiteSpace(this.HostName) ? "Host" : this.HostName;
        this.State = SyncState.Viewing;
        this.StatusText = $"Following \"{this.RoomName}\".";
    }

    private void OnRoomUrlHostGranted()
    {
        this.roomUrlRelay = true;
        this.wantHost = true;
        this.HostName = this.displayName;
        this.HostPresent = true;
        this.State = SyncState.Hosting;
        this.StatusText = $"Hosting shell {this.currentRoom}.";
        this.lastSentUrl = string.Empty;
    }

    private void OnServerError(string code, string? text)
    {
        switch (code)
        {
            case "invalid_room":
            case "room_expired":
                // A stale join/resume intent (the shell died while we were reconnecting).
                this.currentRoom = null;
                this.hostToken = null;
                this.wantHost = false;
                if (this.State is SyncState.Connecting or SyncState.Viewing or SyncState.Hosting)
                {
                    this.State = SyncState.Connected;
                }

                this.StatusText = code == "invalid_room" ? "That shell no longer exists." : "That shell expired.";
                break;

            case "not_host":
                if (this.State == SyncState.Connecting)
                {
                    // Resume failed; fall back to the lobby rather than dying.
                    this.currentRoom = null;
                    this.hostToken = null;
                    this.wantHost = false;
                    this.State = SyncState.Connected;
                    this.StatusText = "Could not reclaim your shell — it was lost. You can host a new one.";
                }

                break;

            default:
                this.StatusText = text ?? $"Relay error: {code}.";
                break;
        }
    }

    private void QueueApply(JsonElement state)
    {
        var url = state.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        this.pendingApply = new PageState(
            url,
            state.TryGetProperty("sx", out var sx) ? sx.GetInt32() :
            state.TryGetProperty("scrollX", out var scrollX) ? scrollX.GetInt32() : 0,
            state.TryGetProperty("sy", out var sy) ? sy.GetInt32() :
            state.TryGetProperty("scrollY", out var scrollY) ? scrollY.GetInt32() : 0,
            state.TryGetProperty("vw", out var vw) ? vw.GetInt32() :
            state.TryGetProperty("viewportW", out var viewportW) ? viewportW.GetInt32() : 0,
            state.TryGetProperty("vh", out var vh) ? vh.GetInt32() :
            state.TryGetProperty("viewportH", out var viewportH) ? viewportH.GetInt32() : 0,
            state.TryGetProperty("dw", out var dw) ? dw.GetInt32() :
            state.TryGetProperty("documentW", out var documentW) ? documentW.GetInt32() : 0,
            state.TryGetProperty("dh", out var dh) ? dh.GetInt32() :
            state.TryGetProperty("documentH", out var documentH) ? documentH.GetInt32() : 0,
            state.TryGetProperty("z", out var z) ? z.GetDouble() :
            state.TryGetProperty("zoom", out var zoom) ? zoom.GetDouble() : 1.0,
            state.TryGetProperty("mt", out var mt) ? mt.GetDouble() :
            state.TryGetProperty("mediaTime", out var mediaTime) ? mediaTime.GetDouble() : 0,
            state.TryGetProperty("md", out var md) ? md.GetDouble() :
            state.TryGetProperty("mediaDuration", out var mediaDuration) ? mediaDuration.GetDouble() : 0,
            state.TryGetProperty("mp", out var mp) ? mp.GetBoolean() :
            state.TryGetProperty("mediaPaused", out var mediaPaused) && mediaPaused.GetBoolean(),
            state.TryGetProperty("mr", out var mr) ? mr.GetDouble() :
            state.TryGetProperty("mediaRate", out var mediaRate) ? mediaRate.GetDouble() : 1.0,
            state.TryGetProperty("mm", out var mm) ? mm.GetBoolean() :
            state.TryGetProperty("mediaMuted", out var mediaMuted) && mediaMuted.GetBoolean(),
            state.TryGetProperty("mf", out var mf) ? mf.GetBoolean() :
            state.TryGetProperty("mediaFullscreen", out var mediaFullscreen) && mediaFullscreen.GetBoolean(),
            ReadLayout(state),
            state.TryGetProperty("ts", out var ts) && ts.TryGetInt64(out var tsValue) ? tsValue : 0);
    }

    private static RoomScreenLayout? ReadLayout(JsonElement state)
    {
        if (!state.TryGetProperty("layout", out var layout) || layout.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        static float Float(JsonElement element, string property, float fallback = 0f) =>
            element.TryGetProperty(property, out var value) && value.TryGetSingle(out var result)
                ? result
                : fallback;

        static bool Bool(JsonElement element, string property, bool fallback = false) =>
            element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : fallback;

        return new RoomScreenLayout(
            Bool(layout, nameof(RoomScreenLayout.Enabled), true),
            Float(layout, nameof(RoomScreenLayout.X)),
            Float(layout, nameof(RoomScreenLayout.Y)),
            Float(layout, nameof(RoomScreenLayout.Z)),
            Float(layout, nameof(RoomScreenLayout.Rotation)),
            Float(layout, nameof(RoomScreenLayout.Width), 4f),
            Float(layout, nameof(RoomScreenLayout.Height), 2.25f),
            Bool(layout, nameof(RoomScreenLayout.LockAspect), true),
            Float(layout, nameof(RoomScreenLayout.Elevation)),
            Float(layout, nameof(RoomScreenLayout.Push)),
            Float(layout, nameof(RoomScreenLayout.Distance), 3f),
            Float(layout, nameof(RoomScreenLayout.HeightOffset), 1.6f),
            Bool(layout, nameof(RoomScreenLayout.ActorOcclusion), true),
            Float(layout, nameof(RoomScreenLayout.OcclusionPadding), 8f));
    }

    private Uri BuildConnectUri()
    {
        if (!this.roomUrlRelay)
        {
            return new Uri(this.relayUrl);
        }

        var builder = new UriBuilder(this.relayUrl);
        if (!builder.Path.TrimEnd('/').EndsWith("/ws", StringComparison.OrdinalIgnoreCase))
        {
            builder.Path = builder.Path.TrimEnd('/') + "/ws";
        }

        if (!string.IsNullOrWhiteSpace(this.currentRoom))
        {
            builder.Query = $"room={Uri.EscapeDataString(this.currentRoom)}";
        }
        else
        {
            builder.Query = string.Empty;
        }

        return builder.Uri;
    }

    private Uri BuildRoomsHttpUri()
    {
        var baseUri = new Uri(this.relayUrl);
        var builder = new UriBuilder(baseUri)
        {
            Scheme = baseUri.Scheme == "wss" ? "https" : "http",
            Path = "/api/rooms",
            Query = string.Empty,
        };
        return builder.Uri;
    }

    private static string MapRoomsResponse(JsonElement root)
    {
        if (root.TryGetProperty("t", out var type) && type.GetString() == "rooms")
        {
            return root.GetRawText();
        }

        if (!root.TryGetProperty("rooms", out var rooms) || rooms.ValueKind != JsonValueKind.Array)
        {
            return "{\"t\":\"rooms\",\"rooms\":[]}";
        }

        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("t", "rooms");
            writer.WritePropertyName("rooms");
            writer.WriteStartArray();
            foreach (var room in rooms.EnumerateArray())
            {
                writer.WriteStartObject();
                writer.WriteString("room", GetString(room, "room", GetString(room, "id", string.Empty)));
                writer.WriteString("roomName", GetString(room, "roomName", GetString(room, "name", string.Empty)));
                writer.WriteString("host", GetString(room, "host", string.Empty));
                writer.WriteNumber("users", GetInt(room, "users", GetInt(room, "clients", 0)));
                writer.WriteNumber("ageSec", GetInt(room, "ageSec", 0));
                writer.WriteBoolean("stale", GetBool(room, "stale", false));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string GetString(JsonElement element, string property, string fallback) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static int GetInt(JsonElement element, string property, int fallback) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : fallback;

    private static bool GetBool(JsonElement element, string property, bool fallback) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static bool LooksLikeRoomUrlRelay(Uri uri) =>
        uri.AbsolutePath.TrimEnd('/').EndsWith("/ws", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Contains("replit.app", StringComparison.OrdinalIgnoreCase);

    private static string NewLocalRoomCode() => Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private static string CleanRoomId(string value) =>
        value.Trim().TrimStart('#').Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    private void Send(object payload)
    {
        var ws = this.socket;
        if (ws is null || ws.State != WebSocketState.Open)
        {
            return;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        _ = Task.Run(async () =>
        {
            await this.sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
            {
                // The receive loop notices the broken socket and drives the reconnect.
            }
            finally
            {
                this.sendLock.Release();
            }
        });
    }
}
