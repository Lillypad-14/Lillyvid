using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace VideoSyncPrototype.Inventory;

/// <summary>
/// Looks up cheapest market-board prices from Universalis, querying at the data-centre
/// scope so each result also carries the world the cheapest listing is on — the same
/// "cheapest across your DC + which server" view fmauNeko's MarketBoardPlugin shows.
/// Keeps a per-item cache and a small fetch throttle so browsing never hammers the API.
/// </summary>
public sealed class MarketPriceService : IDisposable
{
    private const string BaseUrl = "https://universalis.app/api/v2";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly InventorySettings settings;
    private readonly HttpClient httpClient;
    private readonly object cacheLock = new();
    private readonly Dictionary<uint, CachedQuote> priceCache = [];
    private readonly HashSet<uint> fetching = [];
    private readonly TimeSpan fetchDelay = TimeSpan.FromSeconds(0.5);

    // The Universalis query scope — a data-centre name, so listings span the whole DC and
    // report which world each is on.
    private string scope;
    private DateTime lastFetch = DateTime.MinValue;

    public MarketPriceService(InventorySettings settings, string initialScope)
    {
        this.settings = settings;
        this.scope = initialScope;
        this.httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        this.httpClient.DefaultRequestHeaders.Add("User-Agent", "LillypadToolkit/1.0 (inventory)");
    }

    public void UpdateScope(string newScope)
    {
        if (string.IsNullOrEmpty(newScope) || newScope == this.scope)
        {
            return;
        }

        this.scope = newScope;
        lock (this.cacheLock)
        {
            this.priceCache.Clear();
        }
    }

    public bool HasValidCachedPrice(uint itemId)
    {
        lock (this.cacheLock)
        {
            return this.priceCache.TryGetValue(itemId, out var entry) &&
                   DateTime.Now - entry.FetchTime <= TimeSpan.FromMinutes(this.settings.PriceCacheDurationMinutes);
        }
    }

    public bool IsFetchingPrice(uint itemId)
    {
        lock (this.cacheLock)
        {
            return this.fetching.Contains(itemId);
        }
    }

    /// <summary>Writes any cached price + world straight onto the item for display.</summary>
    public void UpdateItemPrice(InventoryItemInfo item)
    {
        lock (this.cacheLock)
        {
            if (this.priceCache.TryGetValue(item.ItemId, out var entry))
            {
                item.MarketPrice = entry.Price;
                item.MarketWorld = entry.World;
                item.MarketPriceFetchTime = entry.FetchTime;
            }
        }
    }

    /// <summary>Picks up to <paramref name="maxCount"/> visible items still needing a price.</summary>
    public List<InventoryItemInfo> GetItemsNeedingPriceFetch(IEnumerable<InventoryItemInfo> visibleItems, int maxCount = 2)
    {
        return visibleItems
            .Where(i => i.CanBeTraded && !this.IsFetchingPrice(i.ItemId) && !this.HasValidCachedPrice(i.ItemId))
            .Take(maxCount)
            .ToList();
    }

    public async Task<bool> FetchPrice(InventoryItemInfo item)
    {
        if (string.IsNullOrEmpty(this.scope) || !item.CanBeTraded || this.IsFetchingPrice(item.ItemId))
        {
            return false;
        }

        if (DateTime.Now - this.lastFetch < this.fetchDelay)
        {
            return false;
        }

        lock (this.cacheLock)
        {
            this.fetching.Add(item.ItemId);
        }

        this.lastFetch = DateTime.Now;

        try
        {
            var quote = await this.QueryUniversalis(item.ItemId, item.IsHQ);
            lock (this.cacheLock)
            {
                this.priceCache[item.ItemId] = quote ?? new CachedQuote(-1L, null, DateTime.Now);
            }

            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"[Inventory] Failed to fetch price for {item.Name}");
            lock (this.cacheLock)
            {
                this.priceCache[item.ItemId] = new CachedQuote(-1L, null, DateTime.Now);
            }

            return false;
        }
        finally
        {
            lock (this.cacheLock)
            {
                this.fetching.Remove(item.ItemId);
            }
        }
    }

    private async Task<CachedQuote?> QueryUniversalis(uint itemId, bool hq)
    {
        // listings=20 is plenty to find the cheapest; hq flag lets us prefer HQ for HQ items.
        var response = await this.httpClient.GetAsync($"{BaseUrl}/{this.scope}/{itemId}?listings=20&entries=0");
        if (!response.IsSuccessStatusCode)
        {
            Plugin.Log.Warning($"[Inventory] Universalis returned {response.StatusCode} for item {itemId}");
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        var parsed = JsonSerializer.Deserialize<UniversalisResponse>(json, JsonOptions);
        if (parsed?.Listings is not { Count: > 0 } listings)
        {
            // No active listings — record "no data" so we don't refetch immediately.
            return new CachedQuote(0L, null, DateTime.Now);
        }

        // Prefer HQ listings for an HQ item, but fall back to all if there are none.
        var pool = hq ? listings.Where(l => l.Hq).ToList() : listings;
        if (pool.Count == 0)
        {
            pool = listings;
        }

        var cheapest = pool.OrderBy(l => l.PricePerUnit).First();
        var world = string.IsNullOrEmpty(cheapest.WorldName) ? this.scope : cheapest.WorldName;
        return new CachedQuote(cheapest.PricePerUnit, world, DateTime.Now);
    }

    public void Dispose() => this.httpClient.Dispose();

    private readonly record struct CachedQuote(long Price, string? World, DateTime FetchTime);

    private sealed class UniversalisResponse
    {
        [JsonPropertyName("listings")]
        public List<UniversalisListing>? Listings { get; set; }
    }

    private sealed class UniversalisListing
    {
        [JsonPropertyName("pricePerUnit")]
        public long PricePerUnit { get; set; }

        [JsonPropertyName("hq")]
        public bool Hq { get; set; }

        [JsonPropertyName("worldName")]
        public string? WorldName { get; set; }
    }
}
