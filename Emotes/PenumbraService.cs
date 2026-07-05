using System;
using System.Collections.Generic;
using Dalamud.Plugin.Ipc;

namespace VideoSyncPrototype.Emotes;

internal sealed class PenumbraService
{
    private ICallGateSubscriber<int>? apiVersion;
    private ICallGateSubscriber<byte, (Guid, string)?>? getCollection;
    private ICallGateSubscriber<int, (bool, bool, (Guid, string))>? getCollectionForObject;
    private ICallGateSubscriber<string>? getModDirectory;
    private ICallGateSubscriber<string, int>? addMod;
    private ICallGateSubscriber<string, string, int>? reloadMod;
    private ICallGateSubscriber<Guid, string, string, (bool, bool, int, IReadOnlyDictionary<string, IReadOnlyList<string>>), string, int, int>? setTemporaryModSettings;
    private ICallGateSubscriber<Guid, string, string, int, int>? removeTemporaryModSettings;
    private ICallGateSubscriber<string, string>? resolvePlayerPath;

    public bool IsAvailable { get; private set; }
    public int ApiVersion { get; private set; }

    public void Initialize()
    {
        try
        {
            this.apiVersion = Plugin.PluginInterface.GetIpcSubscriber<int>("Penumbra.ApiVersion");
            this.ApiVersion = this.apiVersion.InvokeFunc();
            this.IsAvailable = this.ApiVersion >= 5;
            if (!this.IsAvailable)
            {
                return;
            }

            this.getCollection = Plugin.PluginInterface.GetIpcSubscriber<byte, (Guid, string)?>("Penumbra.GetCollection");
            this.getCollectionForObject = Plugin.PluginInterface.GetIpcSubscriber<int, (bool, bool, (Guid, string))>("Penumbra.GetCollectionForObject.V5");
            this.getModDirectory = Plugin.PluginInterface.GetIpcSubscriber<string>("Penumbra.GetModDirectory");
            this.addMod = Plugin.PluginInterface.GetIpcSubscriber<string, int>("Penumbra.AddMod.V5");
            this.reloadMod = Plugin.PluginInterface.GetIpcSubscriber<string, string, int>("Penumbra.ReloadMod.V5");
            this.setTemporaryModSettings = Plugin.PluginInterface.GetIpcSubscriber<Guid, string, string, (bool, bool, int, IReadOnlyDictionary<string, IReadOnlyList<string>>), string, int, int>("Penumbra.SetTemporaryModSettings.V5");
            this.removeTemporaryModSettings = Plugin.PluginInterface.GetIpcSubscriber<Guid, string, string, int, int>("Penumbra.RemoveTemporaryModSettings.V5");
            this.resolvePlayerPath = Plugin.PluginInterface.GetIpcSubscriber<string, string>("Penumbra.ResolvePlayerPath");
        }
        catch (Exception ex)
        {
            this.IsAvailable = false;
            Plugin.Log.Warning($"Penumbra IPC is not available: {ex.Message}");
        }
    }

    public (bool Success, Guid Id, string Name) GetCurrentCollection()
    {
        if (!this.IsAvailable)
        {
            return (false, Guid.Empty, string.Empty);
        }

        try
        {
            if (this.getCollectionForObject is not null)
            {
                var (objectValid, _, collection) = this.getCollectionForObject.InvokeFunc(0);
                if (objectValid && collection.Item1 != Guid.Empty)
                {
                    return (true, collection.Item1, collection.Item2);
                }
            }

            if (this.getCollection is not null)
            {
                var current = this.getCollection.InvokeFunc(0xE2);
                if (current.HasValue)
                {
                    return (true, current.Value.Item1, current.Value.Item2);
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Could not read current Penumbra collection: {ex.Message}");
        }

        return (false, Guid.Empty, string.Empty);
    }

    public string? GetModDirectory()
    {
        if (!this.IsAvailable || this.getModDirectory is null)
        {
            return null;
        }

        try
        {
            return this.getModDirectory.InvokeFunc();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Could not read Penumbra mod directory: {ex.Message}");
            return null;
        }
    }

    public string? ResolvePlayerPath(string gamePath)
    {
        if (!this.IsAvailable || this.resolvePlayerPath is null)
        {
            return null;
        }

        try
        {
            return this.resolvePlayerPath.InvokeFunc(gamePath);
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"Penumbra ResolvePlayerPath failed for '{gamePath}': {ex.Message}");
            return null;
        }
    }

    public bool AddMod(string modDirectory)
    {
        return this.InvokeResultCode(() => this.addMod?.InvokeFunc(modDirectory), $"add Penumbra mod {modDirectory}");
    }

    public bool ReloadMod(string modDirectory)
    {
        return this.InvokeResultCode(() => this.reloadMod?.InvokeFunc(modDirectory, modDirectory), $"reload Penumbra mod {modDirectory}");
    }

    public bool SetTemporaryModSettings(Guid collectionId, string modDirectory, bool enabled, int priority)
    {
        if (!this.IsAvailable || this.setTemporaryModSettings is null)
        {
            return false;
        }

        try
        {
            var emptyOptions = new Dictionary<string, IReadOnlyList<string>>();
            var settings = (false, enabled, priority, (IReadOnlyDictionary<string, IReadOnlyList<string>>)emptyOptions);
            var result = this.setTemporaryModSettings.InvokeFunc(collectionId, modDirectory, modDirectory, settings, "Lillypad Toolkit", 0);
            return result is 0 or 1;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Could not set temporary settings for {modDirectory}: {ex.Message}");
            return false;
        }
    }

    public bool RemoveTemporaryModSettings(Guid collectionId, string modDirectory)
    {
        return this.InvokeResultCode(() => this.removeTemporaryModSettings?.InvokeFunc(collectionId, modDirectory, modDirectory, 0), $"remove temporary settings for {modDirectory}");
    }

    private bool InvokeResultCode(Func<int?> action, string label)
    {
        if (!this.IsAvailable)
        {
            return false;
        }

        try
        {
            var result = action();
            return result is 0 or 1;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Could not {label}: {ex.Message}");
            return false;
        }
    }
}
