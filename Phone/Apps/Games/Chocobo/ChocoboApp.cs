using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using VideoSyncPrototype.Phone.Apps.Games.Framework;
using VideoSyncPrototype.Phone.Core;
using VideoSyncPrototype.Phone.Core.Apps;
using VideoSyncPrototype.Phone.Core.Animation;
using VideoSyncPrototype.Phone.Core.Games;
using VideoSyncPrototype.Phone.Core.Localization;
using VideoSyncPrototype.Phone.Core.Theme;
using VideoSyncPrototype.Phone.Windows;
using VideoSyncPrototype.Phone.Windows.Components;

namespace VideoSyncPrototype.Phone.Apps.Games.Chocobo;

// Chocobo Dash — bet gil on one of six chocobos, watch the race, collect your winnings.
// Odds are derived from each racer's hidden form and carry a house edge; the race itself is
// noisy so favourites usually (but not always) win. Best score tracks your peak bankroll.
internal sealed class ChocoboApp : IMiniGame
{
    private const string GameId = "chocobo";
    private const int StartingGil = 100;
    private const float PaceScale = 0.15f;
    private static readonly int[] ChipValues = { 10, 25, 50 };

    private enum Phase
    {
        Betting,
        Countdown,
        Racing,
        Finished,
    }

    private readonly (string Name, Vector4 Body, Vector4 Jockey)[] roster =
    {
        ("Sunburst", new Vector4(0.98f, 0.82f, 0.28f, 1f), new Vector4(0.22f, 0.45f, 0.86f, 1f)),
        ("Emberfoot", new Vector4(0.91f, 0.44f, 0.29f, 1f), new Vector4(0.96f, 0.86f, 0.32f, 1f)),
        ("Tidecrest", new Vector4(0.40f, 0.68f, 0.96f, 1f), new Vector4(0.97f, 0.56f, 0.24f, 1f)),
        ("Meadowlark", new Vector4(0.52f, 0.80f, 0.42f, 1f), new Vector4(0.88f, 0.32f, 0.56f, 1f)),
        ("Duskfeather", new Vector4(0.56f, 0.46f, 0.74f, 1f), new Vector4(0.32f, 0.86f, 0.72f, 1f)),
        ("Snowplume", new Vector4(0.90f, 0.92f, 0.96f, 1f), new Vector4(0.36f, 0.42f, 0.56f, 1f)),
    };

    private readonly ChocoboRacer[] racers;
    private readonly Random random = new();
    private readonly ParticleSystem particles = new(320);
    private readonly FeedbackFx fx = new();
    private GameStatsStore? statsRef;

    private bool initialized;
    private bool statsLoaded;
    private int bestBankroll;
    private int bankroll;
    private int selected = -1;
    private int betAmount = 25;
    private Phase phase = Phase.Betting;
    private float countdown;
    private int nextRank;
    private int winner = -1;
    private bool playerWon;
    private int payout;
    private bool newBest;
    private float resultAppear;
    private float finishFlourish;

    public ChocoboApp()
    {
        racers = new ChocoboRacer[roster.Length];
        for (var i = 0; i < roster.Length; i++)
        {
            racers[i] = new ChocoboRacer(roster[i].Name, roster[i].Body, roster[i].Jockey);
        }
    }

    public string Id => GameId;
    public Vector4 Accent => AppAccents.For(GameId);
    public string Title => "Chocobo Dash";
    public string Genre => Loc.T(L.Games.GenreArcade);

    public void Open()
    {
        initialized = false;
        statsLoaded = false;
    }

    public void Close()
    {
    }

    public void Dispose()
    {
    }

    private void ResetAll()
    {
        bankroll = StartingGil;
        NewCard();
    }

    private void NewCard()
    {
        // Draw fresh form for every racer, then price the odds off it with a house edge.
        double sum = 0;
        foreach (var racer in racers)
        {
            racer.Strength = 0.72f + (float)random.NextDouble() * 0.56f;
            racer.Pos = 0f;
            racer.Speed = 0f;
            racer.Stride = (float)random.NextDouble() * MathF.PI * 2f;
            racer.Rank = 0;
            racer.Finished = false;
            sum += Math.Pow(racer.Strength, 3);
        }

        foreach (var racer in racers)
        {
            var probability = Math.Pow(racer.Strength, 3) / sum;
            var fair = 0.85 / probability;
            racer.Odds = Math.Clamp((float)(Math.Round(fair * 2.0) / 2.0), 1.5f, 25f);
        }

        phase = Phase.Betting;
        winner = -1;
        nextRank = 0;
        playerWon = false;
        payout = 0;
        newBest = false;
        resultAppear = 0f;
        finishFlourish = 0f;
        if (selected >= 0 && betAmount > bankroll)
        {
            betAmount = Math.Max(0, bankroll);
        }

        betAmount = Math.Clamp(betAmount == 0 ? Math.Min(25, bankroll) : betAmount, 0, bankroll);
    }

    public void Draw(in GameContext context)
    {
        var dt = context.DeltaSeconds;
        var scale = ImGuiHelpers.GlobalScale;
        var theme = context.Theme;
        var body = context.Body;
        statsRef = context.Stats;
        if (!statsLoaded)
        {
            bestBankroll = context.Stats.Get(GameId).BestScore;
            statsLoaded = true;
        }

        if (!initialized)
        {
            ResetAll();
            initialized = true;
        }

        particles.Update(dt);
        fx.Update(dt);
        var drawList = ImGui.GetWindowDrawList();
        GameScene.Ambient(drawList, body, Accent);

        var trackRect = new Rect(new Vector2(body.Min.X + 6f * scale, body.Min.Y + 58f * scale),
            new Vector2(body.Max.X - 6f * scale, body.Max.Y - 6f * scale));

        switch (phase)
        {
            case Phase.Betting:
                DrawBetting(body, theme, scale);
                break;
            case Phase.Countdown:
                countdown -= dt;
                DrawRace(trackRect, theme, scale, dt, false);
                DrawCountdown(trackRect, theme, scale);
                if (countdown <= 0f)
                {
                    phase = Phase.Racing;
                }

                break;
            case Phase.Racing:
                StepRace(trackRect, dt, scale);
                DrawRace(trackRect, theme, scale, dt, true);
                break;
            case Phase.Finished:
                finishFlourish += dt;
                DrawRace(trackRect, theme, scale, dt, false);
                DrawResult(body, theme, dt);
                break;
        }

        particles.Draw(drawList, scale);
        fx.DrawRings(drawList, scale);
        DrawHud(body, theme, scale);

        if (phase != Phase.Finished &&
            GameHud.RestartButton(new Vector2(body.Max.X - 22f * scale, body.Min.Y + 26f * scale), 15f * scale, theme))
        {
            ResetAll();
        }
    }

    // ---- Betting screen -------------------------------------------------------------

    private void DrawBetting(Rect body, PhoneTheme theme, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var top = body.Min.Y + 60f * scale;
        var bottomBar = 92f * scale;
        var listBottom = body.Max.Y - bottomBar;
        var rowGap = 6f * scale;
        var rowHeight = ((listBottom - top) - (racers.Length - 1) * rowGap) / racers.Length;

        Typography.DrawCentered(new Vector2(body.Center.X, body.Min.Y + 48f * scale), "PICK YOUR CHOCOBO",
            theme.TextMuted, TextStyles.Caption1);

        var mouse = ImGui.GetMousePos();
        for (var i = 0; i < racers.Length; i++)
        {
            var racer = racers[i];
            var rowMin = new Vector2(body.Min.X + 8f * scale, top + i * (rowHeight + rowGap));
            var rowMax = new Vector2(body.Max.X - 8f * scale, rowMin.Y + rowHeight);
            var hovered = ImGui.IsMouseHoveringRect(rowMin, rowMax);
            var isSelected = i == selected;
            var radius = 12f * scale;
            var fill = isSelected
                ? GamePalette.Lighten(Accent, 0.05f) with { W = 0.22f }
                : hovered
                    ? GamePalette.Cell with { W = 0.9f }
                    : GamePalette.CellSunken with { W = 0.8f };
            Squircle.Fill(drawList, rowMin, rowMax, radius, ImGui.GetColorU32(fill));
            if (isSelected)
            {
                Squircle.Stroke(drawList, rowMin, rowMax, radius, ImGui.GetColorU32(Accent with { W = 0.9f }),
                    1.6f * scale);
            }

            // Colour chip + mini chocobo.
            var portrait = new Vector2(rowMin.X + rowHeight * 0.62f, (rowMin.Y + rowMax.Y) * 0.5f);
            drawList.AddCircleFilled(portrait, rowHeight * 0.4f,
                ImGui.GetColorU32(racer.Body with { W = 0.22f }));
            ChocoboRenderer.Draw(drawList, portrait + new Vector2(0f, rowHeight * 0.06f), rowHeight * 0.34f, racer.Body,
                racer.Jockey, 0f, false, 0f);

            var textX = rowMin.X + rowHeight * 1.25f;
            Typography.Draw(new Vector2(textX, rowMin.Y + rowHeight * 0.24f), racer.Name, theme.TextStrong,
                TextStyles.Headline);
            Typography.Draw(new Vector2(textX, rowMin.Y + rowHeight * 0.56f),
                $"Win {Gil((int)MathF.Round(betAmount * racer.Odds))}", theme.TextMuted, TextStyles.Caption1);

            // Odds pill on the right.
            var oddsText = "x" + racer.Odds.ToString("0.0", CultureInfo.InvariantCulture);
            var oddsSize = Typography.Measure(oddsText, TextStyles.Headline);
            var pillHalf = new Vector2(oddsSize.X * 0.5f + 12f * scale, 15f * scale);
            var pillCenter = new Vector2(rowMax.X - pillHalf.X - 8f * scale, (rowMin.Y + rowMax.Y) * 0.5f);
            Squircle.Fill(drawList, pillCenter - pillHalf, pillCenter + pillHalf, pillHalf.Y,
                ImGui.GetColorU32(racer.Body with { W = 0.20f }));
            Typography.DrawCentered(pillCenter, oddsText, GamePalette.Lighten(racer.Body, 0.25f), TextStyles.Headline);

            if (hovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    selected = i;
                }
            }
        }

        DrawBetBar(body, theme, scale, bottomBar);
    }

    private void DrawBetBar(Rect body, PhoneTheme theme, float scale, float barHeight)
    {
        var drawList = ImGui.GetWindowDrawList();
        var barTop = body.Max.Y - barHeight;
        var chipY = barTop + 24f * scale;
        var chipWidth = 52f * scale;
        var chipGap = 8f * scale;
        var chipCount = ChipValues.Length + 1;
        var totalWidth = chipCount * chipWidth + (chipCount - 1) * chipGap;
        var startX = body.Center.X - totalWidth * 0.5f;

        for (var i = 0; i < ChipValues.Length; i++)
        {
            var center = new Vector2(startX + i * (chipWidth + chipGap) + chipWidth * 0.5f, chipY);
            var active = betAmount == ChipValues[i];
            if (Chip(drawList, center, new Vector2(chipWidth, 30f * scale), Gil(ChipValues[i]), active, theme,
                    bankroll >= ChipValues[i]))
            {
                betAmount = Math.Min(ChipValues[i], bankroll);
            }
        }

        var maxCenter = new Vector2(startX + ChipValues.Length * (chipWidth + chipGap) + chipWidth * 0.5f, chipY);
        if (Chip(drawList, maxCenter, new Vector2(chipWidth, 30f * scale), "Max", betAmount == bankroll && bankroll > 0,
                theme, bankroll > 0))
        {
            betAmount = bankroll;
        }

        // Race button.
        var ready = selected >= 0 && betAmount > 0 && betAmount <= bankroll;
        var raceCenter = new Vector2(body.Center.X, barTop + 66f * scale);
        var raceSize = new Vector2(MathF.Min(body.Width - 24f * scale, 240f * scale), 40f * scale);
        var label = selected < 0 ? "Pick a chocobo"
            : betAmount <= 0 ? "Set a bet"
            : $"Race!  ({Gil(betAmount)} on {racers[selected].Name})";
        if (ready)
        {
            if (GameHud.Button(raceCenter, raceSize, label, Accent, theme))
            {
                StartRace();
            }
        }
        else
        {
            var min = raceCenter - raceSize * 0.5f;
            var max = raceCenter + raceSize * 0.5f;
            Squircle.Fill(drawList, min, max, raceSize.Y * 0.5f, ImGui.GetColorU32(GamePalette.CellSunken));
            Typography.DrawCentered(raceCenter, label, theme.TextMuted, TextStyles.Headline);
        }
    }

    private static bool Chip(ImDrawListPtr drawList, Vector2 center, Vector2 size, string label, bool active,
        PhoneTheme theme, bool enabled)
    {
        var half = size * 0.5f;
        var min = center - half;
        var max = center + half;
        var radius = size.Y * 0.5f;
        var hovered = enabled && ImGui.IsMouseHoveringRect(min, max);
        var fill = active ? theme.Accent with { W = 0.85f }
            : enabled ? (hovered ? GamePalette.CellHover : GamePalette.Cell)
            : GamePalette.CellSunken with { W = 0.5f };
        Squircle.Fill(drawList, min, max, radius, ImGui.GetColorU32(fill));
        var ink = active ? GamePalette.InkOn(theme.Accent) : enabled ? theme.TextStrong : theme.TextMuted;
        Typography.DrawCentered(center, label, ink, TextStyles.Subheadline);
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        return hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
    }

    private void StartRace()
    {
        bankroll -= betAmount;
        phase = Phase.Countdown;
        countdown = 2.7f;
        foreach (var racer in racers)
        {
            racer.Pos = 0f;
            racer.Speed = 0f;
            racer.Rank = 0;
            racer.Finished = false;
        }

        nextRank = 0;
        winner = -1;
    }

    // ---- Race -----------------------------------------------------------------------

    private void StepRace(Rect track, float dt, float scale)
    {
        foreach (var racer in racers)
        {
            if (racer.Finished)
            {
                continue;
            }

            var flux = 0.55f + (float)random.NextDouble() * 1.1f;
            if (random.NextDouble() < 0.02)
            {
                flux += 0.9f; // surge
            }

            var target = racer.Strength * flux;
            racer.Speed += (target - racer.Speed) * MathF.Min(1f, dt * 4f);
            racer.Pos += racer.Speed * dt * PaceScale;
            racer.Stride += (0.6f + racer.Speed) * dt * 9f;
            if (racer.Pos >= 1f)
            {
                racer.Pos = 1f;
                racer.Finished = true;
                racer.Rank = ++nextRank;
                OnCross(track, racer, scale);
            }
        }

        if (winner >= 0)
        {
            Resolve();
        }
    }

    private void OnCross(Rect track, ChocoboRacer racer, float scale)
    {
        if (racer.Rank != 1)
        {
            return;
        }

        winner = Array.IndexOf(racers, racer);
        var y = LaneCenter(track, winner);
        var x = track.Max.X - 22f * scale;
        fx.Shockwave(new Vector2(x, y), 90f * scale, GamePalette.Lighten(racer.Body, 0.2f), 0.6f, 3.2f);
        particles.Sparkle(new Vector2(x, y), 12, GamePalette.Lighten(racer.Body, 0.3f), 150f * scale, 2.6f, 0.7f);
    }

    private void Resolve()
    {
        playerWon = winner == selected;
        payout = playerWon ? (int)MathF.Round(betAmount * racers[selected].Odds) : 0;
        bankroll += payout;
        newBest = statsRef?.SubmitScore(GameId, bankroll) ?? false;
        if (newBest)
        {
            bestBankroll = bankroll;
        }

        phase = Phase.Finished;
        resultAppear = 0f;
        finishFlourish = 0f;
        if (playerWon)
        {
            fx.Flash(new Vector4(0.4f, 0.9f, 0.5f, 1f), 0.4f);
        }
    }

    private void DrawRace(Rect track, PhoneTheme theme, float scale, float dt, bool running)
    {
        var drawList = ImGui.GetWindowDrawList();
        var rounding = 14f * scale;
        GameScene.Arena(drawList, track, rounding, scale, Accent);
        var laneCount = racers.Length;
        var laneHeight = track.Height / laneCount;
        var startX = track.Min.X + 34f * scale;
        var finishX = track.Max.X - 20f * scale;

        // Lane bands + dividers.
        for (var i = 0; i < laneCount; i++)
        {
            var laneTop = track.Min.Y + i * laneHeight;
            if (i % 2 == 1)
            {
                drawList.AddRectFilled(new Vector2(track.Min.X + 3f * scale, laneTop),
                    new Vector2(track.Max.X - 3f * scale, laneTop + laneHeight),
                    ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.03f)));
            }

            if (i > 0)
            {
                drawList.AddLine(new Vector2(track.Min.X + 6f * scale, laneTop),
                    new Vector2(track.Max.X - 6f * scale, laneTop),
                    ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.05f)), 1f);
            }
        }

        DrawStartFinish(drawList, track, startX, finishX, scale);

        // Draw racers from back lane to front so overlaps look right.
        for (var i = 0; i < laneCount; i++)
        {
            var racer = racers[i];
            var laneCenterY = LaneCenter(track, i);
            var x = startX + racer.Pos * (finishX - startX);
            var size = laneHeight * 0.5f;
            var bob = running && !racer.Finished ? MathF.Sin(racer.Stride * 2f) * size * 0.06f : 0f;

            if (i == selected)
            {
                ProgressRing.Glow(new Vector2(x, laneCenterY + size * 0.5f), size * 1.1f, Accent, 0.5f);
            }

            if (running && !racer.Finished && racer.Speed > 0.2f)
            {
                particles.Burst(new Vector2(x - size * 0.5f, laneCenterY + size * 0.7f), 1,
                    new Vector4(0.82f, 0.74f, 0.6f, 0.5f), 24f * scale, size * 0.14f, 0.35f, 30f, 1.2f, MathF.PI,
                    ParticleShape.GlowCircle);
            }

            ChocoboRenderer.Draw(drawList, new Vector2(x, laneCenterY), size, racer.Body, racer.Jockey, racer.Stride,
                running && !racer.Finished, bob);

            if (i == selected)
            {
                Typography.DrawCentered(new Vector2(x, laneCenterY - size * 1.05f), "YOU",
                    GamePalette.Lighten(Accent, 0.25f), TextStyles.Caption2);
            }
        }
    }

    private void DrawStartFinish(ImDrawListPtr drawList, Rect track, float startX, float finishX, float scale)
    {
        drawList.AddLine(new Vector2(startX, track.Min.Y + 4f * scale), new Vector2(startX, track.Max.Y - 4f * scale),
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.14f)), 1.5f * scale);

        // Checkered finish line.
        var square = 6f * scale;
        var rows = (int)(track.Height / square);
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < 2; c++)
            {
                if ((r + c) % 2 != 0)
                {
                    continue;
                }

                var min = new Vector2(finishX + c * square, track.Min.Y + 4f * scale + r * square);
                var max = min + new Vector2(square, square);
                if (max.Y > track.Max.Y - 4f * scale)
                {
                    continue;
                }

                drawList.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.5f)));
            }
        }
    }

    private float LaneCenter(Rect track, int lane)
    {
        var laneHeight = track.Height / racers.Length;
        return track.Min.Y + lane * laneHeight + laneHeight * 0.5f;
    }

    private void DrawCountdown(Rect track, PhoneTheme theme, float scale)
    {
        string text;
        Vector4 color;
        if (countdown > 2.0f)
        {
            text = "3";
            color = theme.TextStrong;
        }
        else if (countdown > 1.3f)
        {
            text = "2";
            color = theme.TextStrong;
        }
        else if (countdown > 0.6f)
        {
            text = "1";
            color = theme.TextStrong;
        }
        else
        {
            text = "GO!";
            color = GamePalette.Lighten(Accent, 0.2f);
        }

        var pulse = 1f + 0.12f * Styling.Pulse(Styling.PulseFast);
        Typography.DrawCentered(track.Center + new Vector2(1.5f * scale, 1.5f * scale), text,
            new Vector4(0f, 0f, 0f, 0.4f), TextStyles.LargeTitle.Scale * 1.6f * pulse, TextStyles.LargeTitle.Weight);
        Typography.DrawCentered(track.Center, text, color, TextStyles.LargeTitle.Scale * 1.6f * pulse,
            TextStyles.LargeTitle.Weight);
    }

    // ---- HUD + result ---------------------------------------------------------------

    private void DrawHud(Rect body, PhoneTheme theme, float scale)
    {
        GameHud.Pill(new Vector2(body.Min.X + 48f * scale, body.Min.Y + 26f * scale), "Gil", Gil(bankroll), Accent,
            theme);
        var bestShown = Math.Max(bestBankroll, bankroll);
        GameHud.Pill(new Vector2(body.Center.X, body.Min.Y + 26f * scale), "Best", Gil(bestShown), Accent, theme,
            bankroll >= bestBankroll && bankroll > 0);
    }

    private void DrawResult(Rect body, PhoneTheme theme, float dt)
    {
        resultAppear = MathF.Min(1f, resultAppear + dt * 3.2f);
        var win = playerWon;
        var broke = bankroll <= 0;
        var title = broke ? "Out of gil" : win ? "You win!" : "You lose";
        var titleColor = broke ? theme.Danger : win ? new Vector4(0.42f, 0.86f, 0.5f, 1f) : theme.TextStrong;
        var label = win ? "Winnings" : "Payout";
        var value = win ? payout.ToString(CultureInfo.InvariantCulture) : "0";
        var winnerName = winner >= 0 ? racers[winner].Name : "?";
        var secondary = win
            ? $"{winnerName} • x{racers[winner].Odds.ToString("0.0", CultureInfo.InvariantCulture)}"
            : $"{winnerName} took the race";
        var buttonLabel = broke ? "New purse" : "Next race";

        var result = new GameResult(title, titleColor, label, value, secondary, newBest, buttonLabel);
        if (GameOverlay.Draw(body, theme, Accent, resultAppear, result))
        {
            if (broke)
            {
                bankroll = StartingGil;
            }

            NewCard();
        }
    }

    private static string Gil(int amount) => amount.ToString(CultureInfo.InvariantCulture) + "g";
}
