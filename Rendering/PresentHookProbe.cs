using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using KernelDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;
using SceneCameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager;

namespace VideoSyncPrototype.Rendering;

/// <summary>
/// Hooks IDXGISwapChain::Present and ID3D11DeviceContext::OMSetRenderTargets.
/// Like PyonPix, the world screen is drawn from inside the OMSetRenderTargets
/// detour at the moment the game binds its main scene color target together with
/// the real scene depth buffer, so the quad is depth-tested against the world and
/// composited before the game's post-processing and UI.
/// </summary>
public sealed unsafe class PresentHookProbe : IDisposable
{
    private static readonly Guid Id3D11Texture2DGuid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid IdxgiDeviceGuid = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    private sealed class RtvInfo
    {
        public bool Eligible;
        public long Calls;
        public long PairedWithDsv;
        public DXGI_FORMAT Format;
    }

    private readonly NativeTestRenderer nativeTestRenderer = new();
    private readonly object trackingLock = new();
    private readonly Dictionary<nint, RtvInfo> rtvCache = new();
    private readonly List<nint> rtvDiscoveryOrder = new();
    private readonly List<nint> eligibleDsvOrder = new();
    private readonly HashSet<nint> eligibleDsvs = new();
    private readonly HashSet<nint> rejectedDsvs = new();
    private Hook<PresentDelegate>? presentHook;
    private Hook<OMSetRenderTargetsDelegate>? omSetRenderTargetsHook;
    private long presentCount;
    private long omSetRenderTargetsCount;
    private long sceneDrawCount;
    private long lastDrawnPresentIndex = -1;
    private nint swapChainAddress;
    private nint presentAddress;
    private nint deviceContextAddress;
    private nint omSetRenderTargetsAddress;
    private nint mainRtvAddress;
    private uint trackedWidth;
    private uint trackedHeight;
    private bool inNativeDraw;
    private string lastError = string.Empty;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PresentDelegate(nint swapChain, uint syncInterval, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void OMSetRenderTargetsDelegate(nint deviceContext, uint numViews, nint renderTargetViews, nint depthStencilView);

    public bool IsInstalled => this.presentHook is { IsDisposed: false } || this.omSetRenderTargetsHook is { IsDisposed: false };

    public string Status
    {
        get
        {
            var state = this.presentHook switch
            {
                null => "not installed",
                { IsDisposed: true } => "disposed",
                { IsEnabled: true } => "enabled",
                _ => "disabled",
            };

            var error = string.IsNullOrWhiteSpace(this.lastError) ? string.Empty : $"\nLast error: {this.lastError}";
            var omState = this.omSetRenderTargetsHook switch
            {
                null => "not installed",
                { IsDisposed: true } => "disposed",
                { IsEnabled: true } => "enabled",
                _ => "disabled",
            };

            int rtvCount;
            int eligibleRtvCount;
            int dsvCount;
            var mainRtvFormat = "n/a";
            lock (this.trackingLock)
            {
                rtvCount = this.rtvCache.Count;
                eligibleRtvCount = 0;
                foreach (var info in this.rtvCache.Values)
                {
                    if (info.Eligible)
                    {
                        eligibleRtvCount++;
                    }
                }

                dsvCount = this.eligibleDsvs.Count;
                if (this.mainRtvAddress != 0 && this.rtvCache.TryGetValue(this.mainRtvAddress, out var mainInfo))
                {
                    mainRtvFormat = mainInfo.Format.ToString();
                }
            }

            return
                $"Present hook: {state}\n" +
                $"Swapchain: {Ptr(this.swapChainAddress)}\n" +
                $"Present: {Ptr(this.presentAddress)}\n" +
                $"Frames seen: {this.presentCount}\n" +
                $"OMSetRenderTargets hook: {omState}\n" +
                $"Device context: {Ptr(this.deviceContextAddress)}\n" +
                $"OMSetRenderTargets: {Ptr(this.omSetRenderTargetsAddress)}\n" +
                $"Target binds seen: {this.omSetRenderTargetsCount}\n" +
                $"Tracked RTVs: {rtvCount} ({eligibleRtvCount} device-sized)\n" +
                $"Scene depth buffers: {dsvCount}\n" +
                $"Main scene RTV: {Ptr(this.mainRtvAddress)} ({mainRtvFormat})\n" +
                $"Scene draws: {this.sceneDrawCount}\n" +
                $"{this.nativeTestRenderer.Status}{error}";
        }
    }

    public bool NativeTestDrawEnabled
    {
        get => this.nativeTestRenderer.Enabled;
        set
        {
            if (value && !this.nativeTestRenderer.Enabled)
            {
                this.ResetTracking();
            }

            this.nativeTestRenderer.Enabled = value;
        }
    }

    public bool NativeScreenSpaceProbeEnabled
    {
        get => this.nativeTestRenderer.ScreenSpaceProbeEnabled;
        set => this.nativeTestRenderer.ScreenSpaceProbeEnabled = value;
    }

    public int NativeUpscaleFilter
    {
        get => this.nativeTestRenderer.UpscaleFilter;
        set => this.nativeTestRenderer.UpscaleFilter = value;
    }

    public float NativeUpscaleSharpness
    {
        get => this.nativeTestRenderer.UpscaleSharpness;
        set => this.nativeTestRenderer.UpscaleSharpness = value;
    }

    public float NativeDebandStrength
    {
        get => this.nativeTestRenderer.DebandStrength;
        set => this.nativeTestRenderer.DebandStrength = value;
    }

    public float NativeArtifactStrength
    {
        get => this.nativeTestRenderer.ArtifactStrength;
        set => this.nativeTestRenderer.ArtifactStrength = value;
    }

    public float NativeCompareSplit
    {
        get => this.nativeTestRenderer.CompareSplit;
        set => this.nativeTestRenderer.CompareSplit = value;
    }

    public int NativeSourceWidth => this.nativeTestRenderer.SourceWidth;

    public int NativeSourceHeight => this.nativeTestRenderer.SourceHeight;

    public void SetNativeQuad(Vector3 topLeft, Vector3 topRight, Vector3 bottomRight, Vector3 bottomLeft)
    {
        this.nativeTestRenderer.SetQuad(topLeft, topRight, bottomRight, bottomLeft);
    }

    public void SetNativeDecorations(IReadOnlyList<NativeDecorQuad> decorations)
    {
        this.nativeTestRenderer.SetDecorations(decorations);
    }

    public void ClearNativeQuad()
    {
        this.nativeTestRenderer.ClearQuad();
    }

    public void SetNativeTexture(nint shaderResourceView)
    {
        this.nativeTestRenderer.SetTexture(shaderResourceView);
    }

    public void SetNativeSharedTexture(nint sharedHandle)
    {
        this.nativeTestRenderer.SetSharedTextureHandle(sharedHandle);
    }

    public bool TryGetGameAdapterLuid(out long luid, out string adapterName)
    {
        luid = 0;
        adapterName = string.Empty;

        var kernelDevice = KernelDevice.Instance();
        if (kernelDevice is null || kernelDevice->D3D11DeviceContext is null)
        {
            return false;
        }

        ID3D11Device* d3dDevice = null;
        void* dxgiDevice = null;
        void* adapter = null;
        try
        {
            ((ID3D11DeviceContext*)kernelDevice->D3D11DeviceContext)->GetDevice(&d3dDevice);
            if (d3dDevice is null)
            {
                return false;
            }

            var dxgiDeviceGuid = IdxgiDeviceGuid;
            var hr = ((IUnknown*)d3dDevice)->QueryInterface(&dxgiDeviceGuid, &dxgiDevice);
            if (hr.Value < 0 || dxgiDevice is null)
            {
                return false;
            }

            var dxgiDeviceVtbl = *(nint**)dxgiDevice;
            var getAdapter = (delegate* unmanaged[Stdcall]<void*, void**, int>)dxgiDeviceVtbl[7];
            var adapterHr = getAdapter(dxgiDevice, &adapter);
            if (adapterHr < 0 || adapter is null)
            {
                return false;
            }

            var adapterVtbl = *(nint**)adapter;
            var getDesc = (delegate* unmanaged[Stdcall]<void*, DxgiAdapterDesc*, int>)adapterVtbl[8];
            DxgiAdapterDesc desc = default;
            var descHr = getDesc(adapter, &desc);
            if (descHr < 0)
            {
                return false;
            }

            luid = ((long)desc.AdapterLuid.HighPart << 32) | desc.AdapterLuid.LowPart;
            adapterName = desc.GetDescription();
            return true;
        }
        finally
        {
            Release((IUnknown*)adapter);
            Release((IUnknown*)dxgiDevice);
            Release(d3dDevice);
        }
    }

    public bool TryInstall()
    {
        if (this.IsInstalled)
        {
            this.lastError = string.Empty;
            return true;
        }

        try
        {
            this.lastError = string.Empty;
            if (!TryGetPresentAddress(out var swapChain, out var present))
            {
                this.lastError = "The game DXGI swapchain or Present vtable entry was not available yet.";
                return false;
            }

            if (!TryGetOMSetRenderTargetsAddress(out var deviceContext, out var omSetRenderTargets))
            {
                this.lastError = "The game D3D11 device context or OMSetRenderTargets vtable entry was not available yet.";
                return false;
            }

            this.swapChainAddress = swapChain;
            this.presentAddress = present;
            this.deviceContextAddress = deviceContext;
            this.omSetRenderTargetsAddress = omSetRenderTargets;
            this.presentHook = Plugin.GameInterop.HookFromAddress<PresentDelegate>(
                present,
                this.PresentDetour,
                IGameInteropProvider.HookBackend.Automatic);
            this.omSetRenderTargetsHook = Plugin.GameInterop.HookFromAddress<OMSetRenderTargetsDelegate>(
                omSetRenderTargets,
                this.OMSetRenderTargetsDetour,
                IGameInteropProvider.HookBackend.Automatic);
            this.presentHook.Enable();
            this.omSetRenderTargetsHook.Enable();
            return true;
        }
        catch (Exception ex)
        {
            this.lastError = $"{ex.GetType().Name}: {ex.Message}";
            Plugin.Log.Warning(ex, "Could not install Present hook probe.");
            this.DisposeHook();
            return false;
        }
    }

    public void Disable()
    {
        try
        {
            this.presentHook?.Disable();
            this.omSetRenderTargetsHook?.Disable();
        }
        catch (Exception ex)
        {
            this.lastError = $"{ex.GetType().Name}: {ex.Message}";
            Plugin.Log.Warning(ex, "Could not disable Present hook probe.");
        }
    }

    public void Dispose()
    {
        this.nativeTestRenderer.Dispose();
        this.DisposeHook();
    }

    private void ResetTracking()
    {
        lock (this.trackingLock)
        {
            this.rtvCache.Clear();
            this.rtvDiscoveryOrder.Clear();
            this.eligibleDsvOrder.Clear();
            this.eligibleDsvs.Clear();
            this.rejectedDsvs.Clear();
            this.mainRtvAddress = 0;
            this.lastDrawnPresentIndex = -1;
        }
    }

    private int PresentDetour(nint swapChain, uint syncInterval, uint flags)
    {
        this.presentCount++;

        if (this.nativeTestRenderer.ScreenSpaceProbeEnabled && !this.inNativeDraw)
        {
            this.inNativeDraw = true;
            try
            {
                if (this.TryGetSwapChainBackBufferTarget(
                        swapChain,
                        (ID3D11DeviceContext*)this.deviceContextAddress,
                        out var renderTarget,
                        out var width,
                        out var height))
                {
                    this.nativeTestRenderer.RenderScreenProbe((ID3D11DeviceContext*)this.deviceContextAddress, renderTarget, width, height);
                }

                Release(renderTarget);
            }
            finally
            {
                this.inNativeDraw = false;
            }
        }

        return this.presentHook?.Original(swapChain, syncInterval, flags) ?? 0;
    }

    private void OMSetRenderTargetsDetour(nint deviceContext, uint numViews, nint renderTargetViews, nint depthStencilView)
    {
        this.omSetRenderTargetsHook?.Original(deviceContext, numViews, renderTargetViews, depthStencilView);
        this.omSetRenderTargetsCount++;

        // The hooked vtable slot is shared by every device context, so only track the
        // game's immediate context, and ignore binds we issue during our own draw.
        if (this.inNativeDraw || deviceContext != this.deviceContextAddress)
        {
            return;
        }

        if (!this.nativeTestRenderer.Enabled || !this.nativeTestRenderer.HasQuad)
        {
            return;
        }

        try
        {
            this.TrackAndDraw(deviceContext, numViews, renderTargetViews, depthStencilView);
        }
        catch (Exception ex)
        {
            this.lastError = $"{ex.GetType().Name}: {ex.Message}";
            this.nativeTestRenderer.Enabled = false;
            Plugin.Log.Warning(ex, "Scene pass tracking failed; disabled the native world screen.");
        }
    }

    private void TrackAndDraw(nint deviceContext, uint numViews, nint renderTargetViews, nint depthStencilView)
    {
        var device = KernelDevice.Instance();
        if (device is null)
        {
            return;
        }

        var width = device->Width;
        var height = device->Height;
        if (width == 0 || height == 0)
        {
            return;
        }

        if (width != this.trackedWidth || height != this.trackedHeight)
        {
            this.ResetTracking();
            this.trackedWidth = width;
            this.trackedHeight = height;
        }

        if (numViews == 0 || renderTargetViews == 0)
        {
            return;
        }

        // Faithful port of PyonPix's RendererService.OMSetRenderTargetsDetour with its
        // default settings (Format/ResourceBindingType/DepthMode Auto, RenderMode PreDraw).
        bool shouldDraw;
        nint mainRtv;
        nint[] depthStencils;
        lock (this.trackingLock)
        {
            var dsvEligible = false;
            if (depthStencilView != 0)
            {
                if (this.eligibleDsvs.Contains(depthStencilView))
                {
                    dsvEligible = true;
                }
                else if (!this.rejectedDsvs.Contains(depthStencilView))
                {
                    // PyonPix: the scene depth buffer is device-sized, shader-bindable
                    // and R24G8_TYPELESS.
                    if (TryGetViewTextureDesc(depthStencilView, out var dsvDesc) &&
                        dsvDesc.Width == width &&
                        dsvDesc.Height == height &&
                        (dsvDesc.BindFlags & (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE) != 0 &&
                        dsvDesc.Format == DXGI_FORMAT.DXGI_FORMAT_R24G8_TYPELESS)
                    {
                        this.eligibleDsvs.Add(depthStencilView);
                        this.eligibleDsvOrder.Add(depthStencilView);
                        dsvEligible = true;
                    }
                    else
                    {
                        this.rejectedDsvs.Add(depthStencilView);
                    }
                }
            }

            var mainRtvBound = false;
            var slots = (nint*)renderTargetViews;
            for (var i = 0; i < numViews; i++)
            {
                var rtv = slots[i];
                if (rtv == 0)
                {
                    continue;
                }

                if (!this.rtvCache.TryGetValue(rtv, out var info))
                {
                    var hasDesc = TryGetViewTextureDesc(rtv, out var rtvDesc);
                    info = new RtvInfo
                    {
                        Eligible = hasDesc &&
                                   rtvDesc.Width == width &&
                                   rtvDesc.Height == height &&
                                   IsValidSceneRtvFormat(rtvDesc.Format),
                        Format = hasDesc ? rtvDesc.Format : DXGI_FORMAT.DXGI_FORMAT_UNKNOWN,
                    };
                    this.rtvCache[rtv] = info;
                    if (info.Eligible)
                    {
                        this.rtvDiscoveryOrder.Add(rtv);
                    }
                }

                if (!info.Eligible)
                {
                    continue;
                }

                info.Calls++;
                if (depthStencilView != 0)
                {
                    info.PairedWithDsv++;
                }
            }

            // PyonPix walks the discovered RTVs newest-first and picks the first one
            // ever bound together with a depth buffer. Within a frame, discovery order
            // follows the game's pass order, so this lands on the late (post-tonemap)
            // color target instead of the HDR scene buffer, which would glow.
            if (this.lastDrawnPresentIndex != this.presentCount && this.rtvDiscoveryOrder.Count > 0)
            {
                for (var i = this.rtvDiscoveryOrder.Count - 1; i >= 0; i--)
                {
                    var candidate = this.rtvDiscoveryOrder[i];
                    if (this.rtvCache[candidate].PairedWithDsv > 0)
                    {
                        this.mainRtvAddress = candidate;
                        break;
                    }
                }
            }

            for (var i = 0; i < numViews && !mainRtvBound; i++)
            {
                mainRtvBound = slots[i] != 0 && slots[i] == this.mainRtvAddress;
            }

            // PreDraw: draw at the moment the game binds the main RTV together with a
            // scene depth buffer, once per frame.
            shouldDraw = dsvEligible && mainRtvBound &&
                         this.lastDrawnPresentIndex != this.presentCount &&
                         this.eligibleDsvOrder.Count > 0;
            if (shouldDraw)
            {
                this.lastDrawnPresentIndex = this.presentCount;
                mainRtv = this.mainRtvAddress;
                depthStencils = [depthStencilView];
            }
            else
            {
                mainRtv = 0;
                depthStencils = Array.Empty<nint>();
            }
        }

        if (!shouldDraw)
        {
            return;
        }

        if (!TryGetSceneCameraMatrices(out var viewMatrix, out var projectionMatrix))
        {
            return;
        }

        this.inNativeDraw = true;
        try
        {
            this.nativeTestRenderer.RenderScenePass(
                (ID3D11DeviceContext*)deviceContext,
                mainRtv,
                depthStencils,
                width,
                height,
                viewMatrix,
                projectionMatrix);
            this.sceneDrawCount++;
        }
        finally
        {
            this.inNativeDraw = false;
        }
    }

    private void DisposeHook()
    {
        try
        {
            this.presentHook?.Dispose();
            this.omSetRenderTargetsHook?.Dispose();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not dispose Present hook probe.");
        }
        finally
        {
            this.presentHook = null;
            this.omSetRenderTargetsHook = null;
        }
    }

    private static bool TryGetPresentAddress(out nint swapChain, out nint present)
    {
        swapChain = 0;
        present = 0;

        var device = KernelDevice.Instance();
        if (device is null || device->SwapChain is null || device->SwapChain->DXGISwapChain is null)
        {
            return false;
        }

        swapChain = (nint)device->SwapChain->DXGISwapChain;
        var vtbl = *(nint**)swapChain;
        if (vtbl is null)
        {
            return false;
        }

        present = vtbl[8];
        return present != 0;
    }

    private static bool TryGetOMSetRenderTargetsAddress(out nint deviceContext, out nint omSetRenderTargets)
    {
        deviceContext = 0;
        omSetRenderTargets = 0;

        var device = KernelDevice.Instance();
        if (device is null || device->D3D11DeviceContext is null)
        {
            return false;
        }

        deviceContext = (nint)device->D3D11DeviceContext;
        var vtbl = *(nint**)deviceContext;
        if (vtbl is null)
        {
            return false;
        }

        omSetRenderTargets = vtbl[33];
        return omSetRenderTargets != 0;
    }

    private static bool IsValidSceneRtvFormat(DXGI_FORMAT format)
    {
        return format is
            DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM or
            DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_TYPELESS or
            DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM or
            DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_TYPELESS or
            DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_UNORM or
            DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_TYPELESS or
            DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT;
    }

    private static bool TryGetViewTextureDesc(nint viewPtr, out D3D11_TEXTURE2D_DESC desc)
    {
        desc = default;
        if (viewPtr == 0)
        {
            return false;
        }

        ID3D11Resource* resource = null;
        ((ID3D11View*)viewPtr)->GetResource(&resource);
        if (resource is null)
        {
            return false;
        }

        ID3D11Texture2D* texture = null;
        var textureGuid = Id3D11Texture2DGuid;
        var hr = resource->QueryInterface(&textureGuid, (void**)&texture);
        resource->Release();
        if (hr.Value < 0 || texture is null)
        {
            return false;
        }

        D3D11_TEXTURE2D_DESC textureDesc = default;
        texture->GetDesc(&textureDesc);
        texture->Release();
        desc = textureDesc;
        return true;
    }

    private bool TryGetSwapChainBackBufferTarget(
        nint swapChain,
        ID3D11DeviceContext* context,
        out ID3D11RenderTargetView* renderTarget,
        out uint width,
        out uint height)
    {
        renderTarget = null;
        width = 1;
        height = 1;

        if (swapChain == 0 || context is null)
        {
            return false;
        }

        ID3D11Device* device = null;
        ID3D11Texture2D* backBuffer = null;
        try
        {
            context->GetDevice(&device);
            if (device is null)
            {
                this.lastError = "Could not get ID3D11Device for swapchain backbuffer draw.";
                return false;
            }

            var swapChainVtbl = *(nint**)swapChain;
            if (swapChainVtbl is null)
            {
                this.lastError = "Swapchain vtable was null.";
                return false;
            }

            var getBuffer = (delegate* unmanaged[Stdcall]<nint, uint, Guid*, void**, int>)swapChainVtbl[9];
            var surface = (void*)null;
            var textureGuid = Id3D11Texture2DGuid;
            var hr = getBuffer(swapChain, 0, &textureGuid, &surface);
            if (hr < 0 || surface is null)
            {
                this.lastError = $"IDXGISwapChain.GetBuffer failed: 0x{(uint)hr:X8}";
                return false;
            }

            backBuffer = (ID3D11Texture2D*)surface;

            D3D11_TEXTURE2D_DESC desc = default;
            backBuffer->GetDesc(&desc);
            width = Math.Max(1, desc.Width);
            height = Math.Max(1, desc.Height);

            ID3D11RenderTargetView* createdRenderTarget = null;
            var hr2 = device->CreateRenderTargetView((ID3D11Resource*)backBuffer, null, &createdRenderTarget);
            renderTarget = createdRenderTarget;
            if (hr2.Value < 0 || renderTarget is null)
            {
                this.lastError = $"CreateRenderTargetView for swapchain backbuffer failed: 0x{(uint)hr2.Value:X8}";
                return false;
            }

            this.lastError = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            this.lastError = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
        finally
        {
            Release(backBuffer);
            Release(device);
        }
    }

    private static bool TryGetSceneCameraMatrices(out Matrix4x4 viewMatrix, out Matrix4x4 projectionMatrix)
    {
        viewMatrix = Matrix4x4.Identity;
        projectionMatrix = Matrix4x4.Identity;

        var cameraManager = SceneCameraManager.Instance();
        var sceneCamera = cameraManager is null ? null : cameraManager->CurrentCamera;
        if (sceneCamera is null)
        {
            return false;
        }

        var renderCamera = sceneCamera->RenderCamera;
        if (renderCamera is null)
        {
            return false;
        }

        // PyonPix uses the scene camera's view matrix with M44 forced to 1 and the
        // render camera's projection matrix.
        viewMatrix = sceneCamera->ViewMatrix;
        viewMatrix.M44 = 1f;
        projectionMatrix = renderCamera->ProjectionMatrix;
        return true;
    }

    private static string Ptr(nint ptr)
    {
        return ptr == 0 ? "null" : $"0x{(nuint)ptr:X}";
    }

    private static void Release<T>(T* ptr)
        where T : unmanaged
    {
        if (ptr is not null)
        {
            ((IUnknown*)ptr)->Release();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct DxgiAdapterDesc
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public Luid AdapterLuid;

        public string GetDescription()
        {
            fixed (char* ptr = this.Description)
            {
                return new string(ptr).TrimEnd('\0');
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }
}
