using System;
using System.Numerics;
using System.Threading;
using Dalamud.Bindings.ImGui;

namespace VideoSyncPrototype.Windows;

public sealed partial class MainWindow
{
    // ---- Networking Sync (Nearby Broadcast Shells) ----------------------------------------
    // Lets plugin users browse together: the host's page URL + scroll position stream through
    // the relay, and viewers' browsers follow automatically. Only lightweight page state is
    // synchronized — never video/audio streams, history, cookies, or anything personal.

    private readonly NetworkSync networkSync = new();

    // Browser page state parsed from the OverlayPlayer status file.
    private string browserPageUrl = string.Empty;
    private int browserScrollX;
    private int browserScrollY;
    private int browserViewportWidth;
    private int browserViewportHeight;
    private int browserDocumentWidth;
    private int browserDocumentHeight;
    private double browserZoom = 1.0;
    private double browserMediaRate = 1.0;
    private bool browserMediaMuted;
    private bool browserMediaFullscreen;

    // Viewer-side application state.
    private NetworkSync.PageState? viewerTarget;
    private string viewerNavUrl = string.Empty;
    private DateTime viewerNavDeadlineUtc = DateTime.MinValue;
    private int viewerNavAttempts;
    private int viewerRequestedScrollX = -1;
    private int viewerRequestedScrollY = -1;
    private DateTime viewerScrollSentUtc = DateTime.MinValue;
    private DateTime viewerMediaSentUtc = DateTime.MinValue;
    private double viewerRequestedMediaTime = -1;
    private bool viewerRequestedMediaPaused = true;
    private double viewerRequestedMediaRate = 1;
    private bool viewerRequestedMediaMuted;
    private bool viewerRequestedMediaFullscreen;

    // UI drafts.
    private string nearbyRoomNameDraft = string.Empty;
    private string nearbyJoinCodeDraft = string.Empty;

    /// <summary>
    /// Called every frame from Plugin.Draw (not from the window Draw), so hosting and
    /// following keep working while the plugin window is closed.
    /// </summary>
    public void TickNetworkSync()
    {
        this.networkSync.Tick();

        var running = this.rendererProcess is not null && !this.rendererProcess.HasExited;
        switch (this.networkSync.State)
        {
            case NetworkSync.SyncState.Hosting when running:
                // The window's Draw normally refreshes status, but sync must keep flowing
                // with the window closed; the refresh is mtime-gated, so this is cheap.
                this.TryUpdatePlaybackStatus();
                this.networkSync.ReportHostPageState(this.BuildCurrentBrowserSyncState());
                break;
            case NetworkSync.SyncState.Viewing:
                if (running)
                {
                    this.TryUpdatePlaybackStatus();
                }

                this.TickViewerApply(running);
                break;
        }
    }

    private void TickViewerApply(bool running)
    {
        if (this.networkSync.TryTakePendingApply(out var latest))
        {
            if (this.viewerTarget is not { } previous || previous.Url != latest.Url)
            {
                this.viewerNavAttempts = 0;
            }

            this.viewerTarget = latest;
        }

        if (this.viewerTarget is not { } target)
        {
            return;
        }

        // The browser isn't up yet: launch it on the host's URL, visible for browsing.
        if (!running)
        {
            this.viewerNavUrl = target.Url;
            this.status = "Shell ready. Use Join host TV to spawn the shared screen.";
            return;
        }

        var now = DateTime.UtcNow;

        // Step 1: navigation. Wait for the page to land before scrolling it.
        if (!UrlsRoughlyEqual(this.browserPageUrl, target.Url))
        {
            var alreadyNavigating = string.Equals(this.viewerNavUrl, target.Url, StringComparison.Ordinal)
                && now < this.viewerNavDeadlineUtc;
            if (!alreadyNavigating)
            {
                // Sites can redirect (consent pages, tracking params), which would loop a
                // strict URL comparison forever — give up after two tries and scroll anyway.
                if (string.Equals(this.viewerNavUrl, target.Url, StringComparison.Ordinal) && this.viewerNavAttempts >= 2)
                {
                    // fall through to scrolling on whatever page we ended up on
                }
                else
                {
                    this.SendNavigationCommand(target.Url);
                    this.viewerNavUrl = target.Url;
                    this.viewerNavDeadlineUtc = now.AddSeconds(8);
                    this.viewerNavAttempts++;
                    return;
                }
            }
            else
            {
                return;
            }
        }

        // Step 2: scroll. Re-send only when the target moved, or occasionally while the
        // page is still far from it (smooth scrolls take time — don't spam mid-animation).
        var (targetScrollX, targetScrollY) = this.NormalizeViewerScrollTarget(target);
        var targetChanged = targetScrollX != this.viewerRequestedScrollX || targetScrollY != this.viewerRequestedScrollY;
        var distance = Math.Abs(this.browserScrollX - targetScrollX) + Math.Abs(this.browserScrollY - targetScrollY);
        var settleElapsed = (now - this.viewerScrollSentUtc).TotalSeconds;
        if ((targetChanged && settleElapsed >= 0.25) || (distance >= 24 && settleElapsed >= 1.5))
        {
            this.SendScrollCommand(targetScrollX, targetScrollY);
            this.viewerRequestedScrollX = targetScrollX;
            this.viewerRequestedScrollY = targetScrollY;
            this.viewerScrollSentUtc = now;
        }

        this.ApplyViewerMediaState(target, now);
    }

    private (int X, int Y) NormalizeViewerScrollTarget(NetworkSync.PageState target)
    {
        static int MapAxis(int hostValue, int hostDocument, int hostViewport, int localDocument, int localViewport)
        {
            var hostMax = Math.Max(0, hostDocument - hostViewport);
            var localMax = Math.Max(0, localDocument - localViewport);
            if (hostMax <= 0 || localMax <= 0)
            {
                return hostValue;
            }

            var ratio = Math.Clamp(hostValue / (double)hostMax, 0, 1);
            return (int)Math.Round(ratio * localMax);
        }

        return (
            MapAxis(target.ScrollX, target.DocumentWidth, target.ViewportWidth, this.browserDocumentWidth, this.browserViewportWidth),
            MapAxis(target.ScrollY, target.DocumentHeight, target.ViewportHeight, this.browserDocumentHeight, this.browserViewportHeight));
    }

    private void ApplyViewerMediaState(NetworkSync.PageState target, DateTime now)
    {
        if (!target.HasMedia || LooksLikeWatch2GetherUrl(target.Url))
        {
            return;
        }

        var targetTime = target.MediaTime;
        if (!target.MediaPaused && target.Timestamp > 0)
        {
            var elapsed = Math.Clamp(
                (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - target.Timestamp) / 1000.0,
                0,
                15);
            targetTime += elapsed * Math.Max(0.25, target.MediaRate);
        }

        if (target.MediaDuration > 0)
        {
            targetTime = Math.Min(targetTime, target.MediaDuration);
        }

        var localTime = this.GetEstimatedPlaybackTime();
        var drift = Math.Abs(localTime - targetTime);
        var sinceLast = (now - this.viewerMediaSentUtc).TotalSeconds;
        var changed = target.MediaPaused != this.viewerRequestedMediaPaused ||
                      Math.Abs(targetTime - this.viewerRequestedMediaTime) >= 1.0 ||
                      Math.Abs(target.MediaRate - this.viewerRequestedMediaRate) >= 0.02 ||
                      target.MediaMuted != this.viewerRequestedMediaMuted ||
                      target.MediaFullscreen != this.viewerRequestedMediaFullscreen;

        if ((changed && sinceLast >= 0.75) || (drift >= 1.25 && sinceLast >= 2.0))
        {
            this.SendMediaSyncCommand(targetTime, target.MediaPaused, target.MediaRate, target.MediaMuted, target.MediaFullscreen);
            this.viewerRequestedMediaTime = targetTime;
            this.viewerRequestedMediaPaused = target.MediaPaused;
            this.viewerRequestedMediaRate = target.MediaRate;
            this.viewerRequestedMediaMuted = target.MediaMuted;
            this.viewerRequestedMediaFullscreen = target.MediaFullscreen;
            this.viewerMediaSentUtc = now;
        }
    }

    private static bool UrlsRoughlyEqual(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return true;
        }

        static string Normalize(string url)
        {
            return url.TrimEnd('/');
        }

        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    private NetworkSync.PageState BuildCurrentBrowserSyncState()
    {
        var estimatedTime = this.GetEstimatedPlaybackTime();
        return new NetworkSync.PageState(
            this.browserPageUrl,
            this.browserScrollX,
            this.browserScrollY,
            this.browserViewportWidth,
            this.browserViewportHeight,
            this.browserDocumentWidth,
            this.browserDocumentHeight,
            this.browserZoom <= 0 ? 1.0 : this.browserZoom,
            estimatedTime,
            this.playbackDuration,
            this.playbackPaused,
            this.browserMediaRate <= 0 ? 1.0 : this.browserMediaRate,
            this.browserMediaMuted,
            this.browserMediaFullscreen,
            this.CaptureCurrentScreenLayout(),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private void SendScrollCommand(int x, int y)
    {
        var seq = Interlocked.Increment(ref this.controlSeq);
        var payload = $"{{\"seq\":{seq},\"cmd\":\"scrollto\",\"x\":{x},\"y\":{y}}}";
        try
        {
            WriteFileAtomic(this.GetControlPath(), payload);
        }
        catch (System.IO.IOException)
        {
            // Renderer is restarting; the next tick retries.
        }
    }

    private void SendMediaSyncCommand(double time, bool paused, double rate, bool muted, bool fullscreen)
    {
        var seq = Interlocked.Increment(ref this.controlSeq);
        var payload = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{{\"seq\":{seq},\"cmd\":\"syncmedia\",\"time\":{Math.Max(0, time):0.###},\"paused\":{(paused ? "true" : "false")},\"rate\":{Math.Clamp(rate, 0.25, 4):0.###},\"muted\":{(muted ? "true" : "false")},\"fullscreen\":{(fullscreen ? "true" : "false")}}}");
        try
        {
            WriteFileAtomic(this.GetControlPath(), payload);
        }
        catch (System.IO.IOException)
        {
            // Renderer is restarting; the next tick retries.
        }
    }

    private static bool LooksLikeWatch2GetherUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Host.Equals("w2g.tv", StringComparison.OrdinalIgnoreCase);

    private string GetLocalDisplayName()
    {
        try
        {
            var name = Plugin.ObjectTable.LocalPlayer?.Name.TextValue;
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }
        catch (Exception)
        {
            // Not on the framework thread or not logged in; fall through.
        }

        return "Viewer";
    }

    private void DisposeNetworkSync() => this.networkSync.Dispose();

    // ---- UI --------------------------------------------------------------------------------

    private void DrawNearbySyncTab(bool running)
    {
        ImGui.TextWrapped("Browse together: the host's page and scroll position stream to everyone " +
            "in the shell. Only the URL and scroll offsets are shared — no video, audio, history, or " +
            "account data ever leaves your PC.");
        ImGui.Spacing();

        if (this.networkSync.State != NetworkSync.SyncState.Disabled)
        {
            if (ImGui.SmallButton("Disable Sync"))
            {
                this.networkSync.Disable();
                this.viewerTarget = null;
            }
        }

        ImGui.Spacing();

        switch (this.networkSync.State)
        {
            case NetworkSync.SyncState.Disabled:
                if (ImGui.Button("Enable Sync", new Vector2(160f, 0f)))
                {
                    this.networkSync.Enable(this.GetNetworkSyncRelayUrl(), this.GetLocalDisplayName());
                }

                ImGui.SameLine();
                ImGui.TextDisabled("Uses Lillypad's public relay by default.");
                break;

            case NetworkSync.SyncState.Connecting:
            case NetworkSync.SyncState.Reconnecting:
                ImGui.TextDisabled(this.networkSync.StatusText);
                break;

            case NetworkSync.SyncState.Connected:
                this.DrawNearbySyncLobby();
                break;

            case NetworkSync.SyncState.Hosting:
                this.DrawNearbySyncHosting(running);
                break;

            case NetworkSync.SyncState.Viewing:
                this.DrawNearbySyncViewing();
                break;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextWrapped(this.networkSync.StatusText);
    }

    private void DrawNearbySyncLobby()
    {
        ImGui.TextUnformatted("Host a broadcast");
        ImGui.SetNextItemWidth(300f);
        ImGui.InputTextWithHint("##nearby-room-name", "Room name (optional)", ref this.nearbyRoomNameDraft, 48);
        ImGui.SameLine();
        if (ImGui.Button("Host Nearby Broadcast"))
        {
            var roomName = string.IsNullOrWhiteSpace(this.nearbyRoomNameDraft)
                ? $"{this.GetLocalDisplayName()}'s broadcast"
                : this.nearbyRoomNameDraft.Trim();
            this.networkSync.HostShell(roomName);
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Join a broadcast");
        ImGui.SetNextItemWidth(140f);
        ImGui.InputTextWithHint("##nearby-join-code", "Code (e.g. QK7PXN)", ref this.nearbyJoinCodeDraft, 8);
        ImGui.SameLine();
        if (ImGui.Button("Join by code") && !string.IsNullOrWhiteSpace(this.nearbyJoinCodeDraft))
        {
            this.networkSync.JoinShell(this.nearbyJoinCodeDraft);
        }

        ImGui.Spacing();
        if (ImGui.Button("Search nearby shells", new Vector2(180f, 0f)))
        {
            this.networkSync.RefreshShellList();
        }

        ImGui.SameLine();
        ImGui.TextDisabled(this.networkSync.UsesRoomCodeRelay
            ? "Finds active rooms from the relay."
            : "Refreshes the relay lobby.");

        ImGui.Spacing();
        if (this.networkSync.Rooms.Count == 0)
        {
            ImGui.TextDisabled(this.networkSync.UsesRoomCodeRelay
                ? "No shells found yet. Host a broadcast, or search after a friend starts one."
                : "No open shells right now. The list refreshes automatically.");
            return;
        }

        if (ImGui.BeginTable("##nearby-shells", 5,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Room", ImGuiTableColumnFlags.WidthStretch, 3f);
            ImGui.TableSetupColumn("Host", ImGuiTableColumnFlags.WidthStretch, 2f);
            ImGui.TableSetupColumn("Users", ImGuiTableColumnFlags.WidthFixed, 44f);
            ImGui.TableSetupColumn("Age", ImGuiTableColumnFlags.WidthFixed, 56f);
            ImGui.TableSetupColumn("##join", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableHeadersRow();

            foreach (var shell in this.networkSync.Rooms)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(shell.Name);
                if (shell.Stale)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled("(host idle)");
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(shell.Host);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(shell.Users.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatShellAge(shell.AgeSec));
                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"Join##{shell.Room}"))
                {
                    this.networkSync.JoinShell(shell.Room);
                }
            }

            ImGui.EndTable();
        }
    }

    private void DrawNearbySyncHosting(bool running)
    {
        ImGui.TextUnformatted($"Hosting \"{this.networkSync.RoomName}\"");
        ImGui.SameLine();
        ImGui.TextDisabled($"code {this.networkSync.RoomId}");
        ImGui.TextUnformatted($"Connected users: {this.networkSync.UserCount}");

        if (running && !string.IsNullOrEmpty(this.browserPageUrl))
        {
            ImGui.TextDisabled($"Broadcasting: {Truncate(this.browserPageUrl, 96)}");
        }
        else
        {
            ImGui.TextWrapped("Start a page from the Watch tab (or the browser window) and it will be " +
                "broadcast to everyone in the shell.");
        }

        ImGui.Spacing();
        if (ImGui.Button("Open fresh browser", new Vector2(170f, 0f)))
        {
            this.OpenFreshBrowserOnScreen();
        }

        ImGui.SameLine();
        if (ImGui.Button("Stop Hosting", new Vector2(140f, 0f)))
        {
            this.networkSync.LeaveShell();
        }
    }

    private void DrawNearbySyncViewing()
    {
        ImGui.TextUnformatted($"Following \"{this.networkSync.RoomName}\"");
        ImGui.SameLine();
        ImGui.TextDisabled($"hosted by {this.networkSync.HostName}");
        ImGui.TextUnformatted($"Connected users: {this.networkSync.UserCount}");
        if (this.networkSync.HostStale)
        {
            ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f),
                this.networkSync.HostPresent
                    ? "The host has gone quiet — they may have connection trouble."
                    : "The host disconnected. Waiting to see if they come back…");
        }

        ImGui.Spacing();
        var canJoinHostTv = this.viewerTarget is not null;
        if (!canJoinHostTv)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("Join host TV", new Vector2(150f, 0f)))
        {
            this.JoinHostTvFromShell();
        }

        if (!canJoinHostTv)
        {
            ImGui.EndDisabled();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Spawn your in-world TV using the host's current URL and initial screen placement. After joining, your own Screen settings stay yours.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Leave Shell", new Vector2(140f, 0f)))
        {
            this.networkSync.LeaveShell();
            this.viewerTarget = null;
        }
    }

    private void JoinHostTvFromShell()
    {
        if (this.viewerTarget is not { } target)
        {
            this.status = "No host TV is available yet.";
            return;
        }

        if (!this.StartRendererBridge(target.Url))
        {
            return;
        }

        if (target.Layout is { } layout)
        {
            this.ApplyRoomScreenLayout(layout);
        }
        else if (this.worldScreenAnchor is null)
        {
            this.PlaceWorldScreenInFrontOfPlayer();
        }

        this.EnableNativeWorldScreen();
        this.playingWatch2GetherRoom = LooksLikeWatch2GetherUrl(target.Url);
        this.currentVideoId = string.Empty;
        if (this.playingWatch2GetherRoom)
        {
            this.ignoredWatch2GetherRoomKeys.Add(new Watch2GetherRoom(target.Url).NormalizedUrl);
        }

        this.viewerNavUrl = target.Url;
        this.viewerNavDeadlineUtc = DateTime.UtcNow.AddSeconds(10);
        this.viewerNavAttempts = 1;
        this.viewerRequestedScrollX = -1;
        this.viewerRequestedScrollY = -1;
        this.viewerScrollSentUtc = DateTime.MinValue;
        this.status = target.Layout is not null
            ? "Joined the host TV with matching initial placement."
            : "Joined the host TV.";
    }

    private static string FormatShellAge(int ageSec) => ageSec switch
    {
        < 60 => $"{ageSec}s",
        < 3600 => $"{ageSec / 60}m",
        _ => $"{ageSec / 3600}h",
    };

    private string GetNetworkSyncRelayUrl() =>
        string.IsNullOrWhiteSpace(this.config.NetworkSyncRelayUrl)
            ? Configuration.DefaultNetworkSyncRelayUrl
            : this.config.NetworkSyncRelayUrl.Trim();

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
