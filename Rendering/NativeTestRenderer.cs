using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using static TerraFX.Interop.DirectX.DirectX;

namespace VideoSyncPrototype.Rendering;

public sealed unsafe class NativeTestRenderer : IDisposable
{
    private const int QuadConstantFloats = 64;
    private const uint QuadConstantBytes = QuadConstantFloats * sizeof(float);
    private static readonly Guid Id3D11Texture2DGuid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    private readonly object quadLock = new();
    private NativeQuad nativeQuad;
    private NativeDecorQuad[] decorations = [];
    private nint textureSrv;
    private nint pendingSharedHandle;
    private nint openedSharedHandle;
    private ID3D11ShaderResourceView* sharedTextureSrv;
    private string sharedTextureError = string.Empty;
    private ID3D11VertexShader* vertexShader;
    private ID3D11PixelShader* pixelShader;
    private ID3D11BlendState* blendState;
    private ID3D11DepthStencilState* depthDisabledState;
    private ID3D11DepthStencilState* sceneDepthGreaterEqualState;
    private ID3D11DepthStencilState* sceneDepthLessEqualState;
    private ID3D11DepthStencilState* sceneDepthGreaterEqualReadState;
    private ID3D11DepthStencilState* sceneDepthLessEqualReadState;
    private ID3D11RasterizerState* rasterizerState;
    private ID3D11SamplerState* samplerState;
    private ID3D11Buffer* quadConstantBuffer;
    private bool resourcesReady;
    private string lastError = string.Empty;

    public bool Enabled { get; set; }

    public bool ScreenSpaceProbeEnabled { get; set; }

    public long DrawAttempts { get; private set; }

    public long DrawSuccesses { get; private set; }

    public string Status
    {
        get
        {
            var error = string.IsNullOrWhiteSpace(this.lastError) ? string.Empty : $"\nDraw error: {this.lastError}";
            var sharedError = string.IsNullOrWhiteSpace(this.sharedTextureError) ? string.Empty : $"\nShared texture error: {this.sharedTextureError}";
            var sharedState = this.sharedTextureSrv is not null
                ? "streaming"
                : this.pendingSharedHandle != 0 ? "pending" : "none";
            return $"Native scene draw: {(this.Enabled ? "enabled" : "disabled")}\nNative screen probe: {(this.ScreenSpaceProbeEnabled ? "enabled" : "disabled")}\nNative quad set: {this.nativeQuad.Enabled}\nNative decorations: {this.decorations.Length}\nNative texture: {(this.textureSrv != 0 ? "bound" : "none")}\nShared texture: {sharedState}\nResources ready: {this.resourcesReady}\nDraw attempts: {this.DrawAttempts}\nDraw successes: {this.DrawSuccesses}{error}{sharedError}";
        }
    }

    public bool HasQuad
    {
        get
        {
            lock (this.quadLock)
            {
                return this.nativeQuad.Enabled;
            }
        }
    }

    public void SetQuad(Vector3 topLeft, Vector3 topRight, Vector3 bottomRight, Vector3 bottomLeft)
    {
        lock (this.quadLock)
        {
            this.nativeQuad = new NativeQuad(true, topLeft, topRight, bottomRight, bottomLeft);
        }
    }

    public void ClearQuad()
    {
        lock (this.quadLock)
        {
            this.nativeQuad = default;
            this.decorations = [];
        }
    }

    public void SetDecorations(IReadOnlyList<NativeDecorQuad> decorationQuads)
    {
        lock (this.quadLock)
        {
            if (decorationQuads.Count == 0)
            {
                this.decorations = [];
                return;
            }

            var next = new NativeDecorQuad[decorationQuads.Count];
            for (var i = 0; i < decorationQuads.Count; i++)
            {
                next[i] = decorationQuads[i];
            }

            this.decorations = next;
        }
    }

    public void SetTexture(nint shaderResourceView)
    {
        lock (this.quadLock)
        {
            if (this.textureSrv == shaderResourceView)
            {
                return;
            }

            if (this.textureSrv != 0)
            {
                ((IUnknown*)this.textureSrv)->Release();
            }

            this.textureSrv = shaderResourceView;
            if (this.textureSrv != 0)
            {
                ((IUnknown*)this.textureSrv)->AddRef();
            }
        }
    }

    /// <summary>
    /// Points the renderer at a D3D11 shared texture produced by another process
    /// (the OverlayPlayer browser). The texture is opened on the game's device the
    /// next time a scene pass draws. Pass 0 to detach.
    /// </summary>
    public void SetSharedTextureHandle(nint sharedHandle)
    {
        lock (this.quadLock)
        {
            this.pendingSharedHandle = sharedHandle;
        }
    }

    /// <summary>
    /// Draws the world quad the way PyonPix's DrawRenderer does: the main scene color
    /// target and each cached scene depth buffer are bound explicitly, and the quad is
    /// depth-tested (reversed-Z GreaterEqual) so it sits in the world.
    /// </summary>
    public void RenderScenePass(
        ID3D11DeviceContext* context,
        nint targetRtv,
        nint[] depthStencils,
        uint width,
        uint height,
        Matrix4x4 viewMatrix,
        Matrix4x4 projectionMatrix)
    {
        if (!this.Enabled || context is null || targetRtv == 0 || depthStencils.Length == 0)
        {
            return;
        }

        this.DrawAttempts++;
        try
        {
            this.EnsureSharedTexture(context);

            NativeQuad quad;
            NativeDecorQuad[] decorations;
            nint srv;
            lock (this.quadLock)
            {
                quad = this.nativeQuad;
                decorations = this.decorations;
                srv = this.sharedTextureSrv is not null ? (nint)this.sharedTextureSrv : this.textureSrv;
                if (srv != 0)
                {
                    ((IUnknown*)srv)->AddRef();
                }
            }

            try
            {
                if (!quad.Enabled)
                {
                    this.lastError = "No native quad has been placed yet. Show/place the world screen first.";
                    return;
                }

                if (!this.EnsureResources(context))
                {
                    return;
                }

                ID3D11RenderTargetView* savedTarget = null;
                ID3D11DepthStencilView* savedDepth = null;
                ID3D11InputLayout* inputLayout = null;
                D3D_PRIMITIVE_TOPOLOGY topology = default;
                ID3D11VertexShader* savedVertexShader = null;
                ID3D11PixelShader* savedPixelShader = null;
                ID3D11BlendState* savedBlendState = null;
                ID3D11DepthStencilState* savedDepthStencilState = null;
                ID3D11RasterizerState* savedRasterizerState = null;
                ID3D11ShaderResourceView* savedShaderResource = null;
                ID3D11SamplerState* savedSampler = null;
                ID3D11Buffer* savedVsConstants = null;
                ID3D11Buffer* savedPsConstants = null;
                uint sampleMask = 0;
                uint stencilRef = 0;
                var blendFactor = stackalloc float[4];
                var viewports = stackalloc D3D11_VIEWPORT[16];
                uint viewportCount = 16;

                try
                {
                    context->OMGetRenderTargets(1, &savedTarget, &savedDepth);
                    context->IAGetInputLayout(&inputLayout);
                    context->IAGetPrimitiveTopology(&topology);
                    context->VSGetShader(&savedVertexShader, null, null);
                    context->PSGetShader(&savedPixelShader, null, null);
                    context->OMGetBlendState(&savedBlendState, blendFactor, &sampleMask);
                    context->OMGetDepthStencilState(&savedDepthStencilState, &stencilRef);
                    context->RSGetState(&savedRasterizerState);
                    context->RSGetViewports(&viewportCount, viewports);
                    context->PSGetShaderResources(0, 1, &savedShaderResource);
                    context->PSGetSamplers(0, 1, &savedSampler);
                    context->VSGetConstantBuffers(0, 1, &savedVsConstants);
                    context->PSGetConstantBuffers(0, 1, &savedPsConstants);

                    context->OMSetBlendState(this.blendState, null, 0xFFFFFFFF);
                    var reversedDepth = UsesReversedDepth(projectionMatrix);
                    context->RSSetState(this.rasterizerState);

                    var viewport = new D3D11_VIEWPORT
                    {
                        TopLeftX = 0,
                        TopLeftY = 0,
                        Width = Math.Max(1, width),
                        Height = Math.Max(1, height),
                        MinDepth = 0,
                        MaxDepth = 1,
                    };
                    context->RSSetViewports(1, &viewport);

                    var constantBuffer = this.quadConstantBuffer;
                    var textureView = (ID3D11ShaderResourceView*)srv;
                    var sampler = this.samplerState;
                    context->IASetInputLayout(null);
                    context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
                    context->VSSetShader(this.vertexShader, null, 0);
                    context->PSSetShader(this.pixelShader, null, 0);
                    context->VSSetConstantBuffers(0, 1, &constantBuffer);
                    context->PSSetConstantBuffers(0, 1, &constantBuffer);
                    context->PSSetSamplers(0, 1, &sampler);

                    var target = (ID3D11RenderTargetView*)targetRtv;
                    foreach (var dsvPtr in depthStencils)
                    {
                        context->OMSetRenderTargets(1, &target, (ID3D11DepthStencilView*)dsvPtr);

                        ID3D11ShaderResourceView* noTexture = null;
                        context->PSSetShaderResources(0, 1, &noTexture);
                        context->OMSetDepthStencilState(
                            reversedDepth
                                ? this.sceneDepthGreaterEqualReadState
                                : this.sceneDepthLessEqualReadState,
                            0);
                        foreach (var decoration in decorations)
                        {
                            this.UpdateDecorConstants(context, decoration, viewMatrix, projectionMatrix);
                            context->Draw(6, 0);
                        }

                        this.UpdateQuadConstants(context, quad, viewMatrix, projectionMatrix, srv != 0);
                        context->PSSetShaderResources(0, 1, &textureView);
                        context->OMSetDepthStencilState(
                            reversedDepth
                                ? this.sceneDepthGreaterEqualState
                                : this.sceneDepthLessEqualState,
                            0);
                        context->Draw(6, 0);
                    }

                    this.DrawSuccesses++;
                    this.lastError = string.Empty;
                }
                finally
                {
                    if (savedTarget is not null)
                    {
                        context->OMSetRenderTargets(1, &savedTarget, savedDepth);
                    }
                    else
                    {
                        context->OMSetRenderTargets(0, null, savedDepth);
                    }

                    context->IASetInputLayout(inputLayout);
                    context->IASetPrimitiveTopology(topology);
                    context->VSSetShader(savedVertexShader, null, 0);
                    context->PSSetShader(savedPixelShader, null, 0);
                    context->OMSetBlendState(savedBlendState, blendFactor, sampleMask);
                    context->OMSetDepthStencilState(savedDepthStencilState, stencilRef);
                    context->RSSetState(savedRasterizerState);
                    if (viewportCount > 0)
                    {
                        context->RSSetViewports(viewportCount, viewports);
                    }

                    context->PSSetShaderResources(0, 1, &savedShaderResource);
                    context->PSSetSamplers(0, 1, &savedSampler);
                    context->VSSetConstantBuffers(0, 1, &savedVsConstants);
                    context->PSSetConstantBuffers(0, 1, &savedPsConstants);

                    Release(savedTarget);
                    Release(savedDepth);
                    Release(inputLayout);
                    Release(savedVertexShader);
                    Release(savedPixelShader);
                    Release(savedBlendState);
                    Release(savedDepthStencilState);
                    Release(savedRasterizerState);
                    Release(savedShaderResource);
                    Release(savedSampler);
                    Release(savedVsConstants);
                    Release(savedPsConstants);
                }
            }
            finally
            {
                if (srv != 0)
                {
                    ((IUnknown*)srv)->Release();
                }
            }
        }
        catch (Exception ex)
        {
            this.lastError = $"{ex.GetType().Name}: {ex.Message}";
            Plugin.Log.Warning(ex, "Native scene renderer failed.");
            this.Enabled = false;
        }
    }

    /// <summary>
    /// Draws the magenta screen-space diagnostic quad at Present time.
    /// </summary>
    public void RenderScreenProbe(
        ID3D11DeviceContext* context,
        ID3D11RenderTargetView* target,
        uint width,
        uint height)
    {
        if (!this.ScreenSpaceProbeEnabled || context is null || target is null)
        {
            return;
        }

        try
        {
            if (!this.EnsureResources(context))
            {
                return;
            }

            ID3D11RenderTargetView* savedTarget = null;
            ID3D11DepthStencilView* savedDepth = null;
            ID3D11InputLayout* inputLayout = null;
            D3D_PRIMITIVE_TOPOLOGY topology = default;
            ID3D11VertexShader* savedVertexShader = null;
            ID3D11PixelShader* savedPixelShader = null;
            ID3D11BlendState* savedBlendState = null;
            ID3D11DepthStencilState* savedDepthStencilState = null;
            ID3D11RasterizerState* savedRasterizerState = null;
            uint sampleMask = 0;
            uint stencilRef = 0;
            var blendFactor = stackalloc float[4];
            var viewports = stackalloc D3D11_VIEWPORT[16];
            uint viewportCount = 16;

            try
            {
                context->OMGetRenderTargets(1, &savedTarget, &savedDepth);
                context->IAGetInputLayout(&inputLayout);
                context->IAGetPrimitiveTopology(&topology);
                context->VSGetShader(&savedVertexShader, null, null);
                context->PSGetShader(&savedPixelShader, null, null);
                context->OMGetBlendState(&savedBlendState, blendFactor, &sampleMask);
                context->OMGetDepthStencilState(&savedDepthStencilState, &stencilRef);
                context->RSGetState(&savedRasterizerState);
                context->RSGetViewports(&viewportCount, viewports);

                this.UpdateProbeConstants(context);

                context->OMSetRenderTargets(1, &target, null);
                context->OMSetBlendState(this.blendState, null, 0xFFFFFFFF);
                context->OMSetDepthStencilState(this.depthDisabledState, 0);
                context->RSSetState(this.rasterizerState);

                var viewport = new D3D11_VIEWPORT
                {
                    TopLeftX = 0,
                    TopLeftY = 0,
                    Width = Math.Max(1, width),
                    Height = Math.Max(1, height),
                    MinDepth = 0,
                    MaxDepth = 1,
                };
                context->RSSetViewports(1, &viewport);

                var constantBuffer = this.quadConstantBuffer;
                context->IASetInputLayout(null);
                context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
                context->VSSetShader(this.vertexShader, null, 0);
                context->PSSetShader(this.pixelShader, null, 0);
                context->VSSetConstantBuffers(0, 1, &constantBuffer);
                context->PSSetConstantBuffers(0, 1, &constantBuffer);
                context->Draw(6, 0);
            }
            finally
            {
                if (savedTarget is not null)
                {
                    context->OMSetRenderTargets(1, &savedTarget, savedDepth);
                }
                else
                {
                    context->OMSetRenderTargets(0, null, null);
                }

                context->IASetInputLayout(inputLayout);
                context->IASetPrimitiveTopology(topology);
                context->VSSetShader(savedVertexShader, null, 0);
                context->PSSetShader(savedPixelShader, null, 0);
                context->OMSetBlendState(savedBlendState, blendFactor, sampleMask);
                context->OMSetDepthStencilState(savedDepthStencilState, stencilRef);
                context->RSSetState(savedRasterizerState);
                if (viewportCount > 0)
                {
                    context->RSSetViewports(viewportCount, viewports);
                }

                Release(savedTarget);
                Release(savedDepth);
                Release(inputLayout);
                Release(savedVertexShader);
                Release(savedPixelShader);
                Release(savedBlendState);
                Release(savedDepthStencilState);
                Release(savedRasterizerState);
            }
        }
        catch (Exception ex)
        {
            this.lastError = $"{ex.GetType().Name}: {ex.Message}";
            Plugin.Log.Warning(ex, "Native screen probe failed.");
            this.ScreenSpaceProbeEnabled = false;
        }
    }

    /// <summary>
    /// Opens the pending shared texture on the game's D3D11 device and wraps it in a
    /// shader resource view. Runs on the render thread inside the scene pass so the
    /// correct device is available.
    /// </summary>
    private void EnsureSharedTexture(ID3D11DeviceContext* context)
    {
        nint pending;
        lock (this.quadLock)
        {
            pending = this.pendingSharedHandle;
        }

        if (pending == this.openedSharedHandle && this.sharedTextureSrv is not null)
        {
            return;
        }

        // Swap the SRV under the lock so a concurrent SetTexture/draw never sees a freed view.
        lock (this.quadLock)
        {
            if (this.sharedTextureSrv is not null)
            {
                this.sharedTextureSrv->Release();
                this.sharedTextureSrv = null;
            }

            this.sharedTextureError = string.Empty;
            if (pending == 0)
            {
                this.openedSharedHandle = 0;
                return;
            }

            ID3D11Device* device = null;
            ID3D11Texture2D* texture = null;
            try
            {
                context->GetDevice(&device);
                if (device is null)
                {
                    this.sharedTextureError = "Could not get ID3D11Device to open the shared texture.";
                    return;
                }

                var textureGuid = Id3D11Texture2DGuid;
                var hr = device->OpenSharedResource(new HANDLE((void*)pending), &textureGuid, (void**)&texture);
                if (hr.Value < 0 || texture is null)
                {
                    this.sharedTextureError = $"OpenSharedResource failed: 0x{(uint)hr.Value:X8}";
                    this.openedSharedHandle = 0;
                    return;
                }

                ID3D11ShaderResourceView* createdSrv = null;
                hr = device->CreateShaderResourceView((ID3D11Resource*)texture, null, &createdSrv);
                if (hr.Value < 0 || createdSrv is null)
                {
                    this.sharedTextureError = $"CreateShaderResourceView for shared texture failed: 0x{(uint)hr.Value:X8}";
                    this.openedSharedHandle = 0;
                    return;
                }

                this.sharedTextureSrv = createdSrv;
                this.openedSharedHandle = pending;
            }
            finally
            {
                Release(texture);
                Release(device);
            }
        }
    }

    public void Dispose()
    {
        this.SetTexture(0);
        this.SetSharedTextureHandle(0);
        lock (this.quadLock)
        {
            if (this.sharedTextureSrv is not null)
            {
                this.sharedTextureSrv->Release();
                this.sharedTextureSrv = null;
            }

            this.openedSharedHandle = 0;
        }

        Release(this.vertexShader);
        Release(this.pixelShader);
        Release(this.blendState);
        Release(this.depthDisabledState);
        Release(this.sceneDepthGreaterEqualState);
        Release(this.sceneDepthLessEqualState);
        Release(this.sceneDepthGreaterEqualReadState);
        Release(this.sceneDepthLessEqualReadState);
        Release(this.rasterizerState);
        Release(this.samplerState);
        Release(this.quadConstantBuffer);
        this.vertexShader = null;
        this.pixelShader = null;
        this.blendState = null;
        this.depthDisabledState = null;
        this.sceneDepthGreaterEqualState = null;
        this.sceneDepthLessEqualState = null;
        this.sceneDepthGreaterEqualReadState = null;
        this.sceneDepthLessEqualReadState = null;
        this.rasterizerState = null;
        this.samplerState = null;
        this.quadConstantBuffer = null;
        this.resourcesReady = false;
    }

    private bool EnsureResources(ID3D11DeviceContext* context)
    {
        if (this.resourcesReady)
        {
            return true;
        }

        ID3D11Device* device = null;
        context->GetDevice(&device);
        if (device is null)
        {
            this.lastError = "Could not get ID3D11Device from the device context.";
            return false;
        }

        ID3DBlob* vertexBlob = null;
        ID3DBlob* pixelBlob = null;
        ID3DBlob* errors = null;
        try
        {
            if (!CompileShader(VertexShaderSource, "main", "vs_4_0", &vertexBlob, &errors))
            {
                this.lastError = ReadBlobText(errors);
                return false;
            }

            Release(errors);
            errors = null;

            if (!CompileShader(PixelShaderSource, "main", "ps_4_0", &pixelBlob, &errors))
            {
                this.lastError = ReadBlobText(errors);
                return false;
            }

            ID3D11VertexShader* createdVertexShader = null;
            var hr = device->CreateVertexShader(vertexBlob->GetBufferPointer(), vertexBlob->GetBufferSize(), null, &createdVertexShader);
            if (Failed(hr))
            {
                this.lastError = $"CreateVertexShader failed: 0x{(uint)hr.Value:X8}";
                return false;
            }

            this.vertexShader = createdVertexShader;

            ID3D11PixelShader* createdPixelShader = null;
            hr = device->CreatePixelShader(pixelBlob->GetBufferPointer(), pixelBlob->GetBufferSize(), null, &createdPixelShader);
            if (Failed(hr))
            {
                this.lastError = $"CreatePixelShader failed: 0x{(uint)hr.Value:X8}";
                return false;
            }

            this.pixelShader = createdPixelShader;

            if (!this.CreateStates(device))
            {
                return false;
            }

            this.resourcesReady = true;
            this.lastError = string.Empty;
            return true;
        }
        finally
        {
            Release(errors);
            Release(vertexBlob);
            Release(pixelBlob);
            device->Release();
        }
    }

    private bool CreateStates(ID3D11Device* device)
    {
        var blendDesc = D3D11_BLEND_DESC.DEFAULT;
        blendDesc.RenderTarget[0].BlendEnable = true;
        blendDesc.RenderTarget[0].SrcBlend = D3D11_BLEND.D3D11_BLEND_SRC_ALPHA;
        blendDesc.RenderTarget[0].DestBlend = D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA;
        blendDesc.RenderTarget[0].BlendOp = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD;
        blendDesc.RenderTarget[0].SrcBlendAlpha = D3D11_BLEND.D3D11_BLEND_ONE;
        blendDesc.RenderTarget[0].DestBlendAlpha = D3D11_BLEND.D3D11_BLEND_ZERO;
        blendDesc.RenderTarget[0].BlendOpAlpha = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD;
        blendDesc.RenderTarget[0].RenderTargetWriteMask = (byte)D3D11_COLOR_WRITE_ENABLE.D3D11_COLOR_WRITE_ENABLE_ALL;
        ID3D11BlendState* createdBlendState = null;
        var hr = device->CreateBlendState(&blendDesc, &createdBlendState);
        if (Failed(hr))
        {
            this.lastError = $"CreateBlendState failed: 0x{(uint)hr.Value:X8}";
            return false;
        }

        this.blendState = createdBlendState;

        var depthDesc = D3D11_DEPTH_STENCIL_DESC.DEFAULT;
        depthDesc.DepthEnable = false;
        depthDesc.DepthWriteMask = D3D11_DEPTH_WRITE_MASK.D3D11_DEPTH_WRITE_MASK_ZERO;
        depthDesc.DepthFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_ALWAYS;
        ID3D11DepthStencilState* createdDepthDisabled = null;
        hr = device->CreateDepthStencilState(&depthDesc, &createdDepthDisabled);
        if (Failed(hr))
        {
            this.lastError = $"CreateDepthStencilState failed: 0x{(uint)hr.Value:X8}";
            return false;
        }

        this.depthDisabledState = createdDepthDisabled;

        var sceneDepthGreaterDesc = D3D11_DEPTH_STENCIL_DESC.DEFAULT;
        sceneDepthGreaterDesc.DepthEnable = true;
        sceneDepthGreaterDesc.DepthWriteMask = D3D11_DEPTH_WRITE_MASK.D3D11_DEPTH_WRITE_MASK_ALL;
        sceneDepthGreaterDesc.DepthFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_GREATER_EQUAL;
        ID3D11DepthStencilState* createdSceneDepthGreater = null;
        hr = device->CreateDepthStencilState(&sceneDepthGreaterDesc, &createdSceneDepthGreater);
        if (Failed(hr))
        {
            this.lastError = $"CreateDepthStencilState (scene GE) failed: 0x{(uint)hr.Value:X8}";
            return false;
        }

        this.sceneDepthGreaterEqualState = createdSceneDepthGreater;

        var sceneDepthLessDesc = D3D11_DEPTH_STENCIL_DESC.DEFAULT;
        sceneDepthLessDesc.DepthEnable = true;
        sceneDepthLessDesc.DepthWriteMask = D3D11_DEPTH_WRITE_MASK.D3D11_DEPTH_WRITE_MASK_ALL;
        sceneDepthLessDesc.DepthFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_LESS_EQUAL;
        ID3D11DepthStencilState* createdSceneDepthLess = null;
        hr = device->CreateDepthStencilState(&sceneDepthLessDesc, &createdSceneDepthLess);
        if (Failed(hr))
        {
            this.lastError = $"CreateDepthStencilState (scene LE) failed: 0x{(uint)hr.Value:X8}";
            return false;
        }

        this.sceneDepthLessEqualState = createdSceneDepthLess;

        sceneDepthGreaterDesc.DepthWriteMask = D3D11_DEPTH_WRITE_MASK.D3D11_DEPTH_WRITE_MASK_ZERO;
        ID3D11DepthStencilState* createdSceneDepthGreaterRead = null;
        hr = device->CreateDepthStencilState(&sceneDepthGreaterDesc, &createdSceneDepthGreaterRead);
        if (Failed(hr))
        {
            this.lastError = $"CreateDepthStencilState (scene GE read) failed: 0x{(uint)hr.Value:X8}";
            return false;
        }

        this.sceneDepthGreaterEqualReadState = createdSceneDepthGreaterRead;

        sceneDepthLessDesc.DepthWriteMask = D3D11_DEPTH_WRITE_MASK.D3D11_DEPTH_WRITE_MASK_ZERO;
        ID3D11DepthStencilState* createdSceneDepthLessRead = null;
        hr = device->CreateDepthStencilState(&sceneDepthLessDesc, &createdSceneDepthLessRead);
        if (Failed(hr))
        {
            this.lastError = $"CreateDepthStencilState (scene LE read) failed: 0x{(uint)hr.Value:X8}";
            return false;
        }

        this.sceneDepthLessEqualReadState = createdSceneDepthLessRead;

        var rasterizerDesc = D3D11_RASTERIZER_DESC.DEFAULT;
        rasterizerDesc.CullMode = D3D11_CULL_MODE.D3D11_CULL_NONE;
        rasterizerDesc.DepthClipEnable = true;
        ID3D11RasterizerState* createdRasterizerState = null;
        hr = device->CreateRasterizerState(&rasterizerDesc, &createdRasterizerState);
        if (Failed(hr))
        {
            this.lastError = $"CreateRasterizerState failed: 0x{(uint)hr.Value:X8}";
            return false;
        }

        this.rasterizerState = createdRasterizerState;

        var samplerDesc = D3D11_SAMPLER_DESC.DEFAULT;
        samplerDesc.Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        samplerDesc.AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP;
        samplerDesc.AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP;
        samplerDesc.AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP;
        ID3D11SamplerState* createdSampler = null;
        hr = device->CreateSamplerState(&samplerDesc, &createdSampler);
        if (Failed(hr))
        {
            this.lastError = $"CreateSamplerState failed: 0x{(uint)hr.Value:X8}";
            return false;
        }

        this.samplerState = createdSampler;

        var constantBufferDesc = new D3D11_BUFFER_DESC
        {
            ByteWidth = QuadConstantBytes,
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
            CPUAccessFlags = 0,
            MiscFlags = 0,
            StructureByteStride = 0,
        };
        ID3D11Buffer* createdConstantBuffer = null;
        hr = device->CreateBuffer(&constantBufferDesc, null, &createdConstantBuffer);
        if (Failed(hr))
        {
            this.lastError = $"CreateBuffer constant buffer failed: 0x{(uint)hr.Value:X8}";
            return false;
        }

        this.quadConstantBuffer = createdConstantBuffer;
        return true;
    }

    private void UpdateQuadConstants(
        ID3D11DeviceContext* context,
        NativeQuad quad,
        Matrix4x4 viewMatrix,
        Matrix4x4 projectionMatrix,
        bool useTexture)
    {
        this.UpdateConstants(
            context,
            quad.TopLeft,
            quad.TopRight,
            quad.BottomRight,
            quad.BottomLeft,
            viewMatrix,
            projectionMatrix,
            new Vector4(0.02f, 0.02f, 0.02f, 1.0f),
            useTexture,
            0.1f);
    }

    private void UpdateDecorConstants(
        ID3D11DeviceContext* context,
        NativeDecorQuad quad,
        Matrix4x4 viewMatrix,
        Matrix4x4 projectionMatrix)
    {
        this.UpdateConstants(
            context,
            quad.TopLeft,
            quad.TopRight,
            quad.BottomRight,
            quad.BottomLeft,
            viewMatrix,
            projectionMatrix,
            quad.Color,
            useTexture: false,
            quad.DepthOffset);
    }

    private void UpdateConstants(
        ID3D11DeviceContext* context,
        Vector3 topLeft,
        Vector3 topRight,
        Vector3 bottomRight,
        Vector3 bottomLeft,
        Matrix4x4 viewMatrix,
        Matrix4x4 projectionMatrix,
        Vector4 color,
        bool useTexture,
        float depthOffset)
    {
        var data = stackalloc float[QuadConstantFloats];
        WritePoint(data, 0, topLeft);
        WritePoint(data, 4, topRight);
        WritePoint(data, 8, bottomRight);
        WritePoint(data, 12, topLeft);
        WritePoint(data, 16, bottomRight);
        WritePoint(data, 20, bottomLeft);
        WriteMatrix(data, 24, viewMatrix);
        WriteMatrix(data, 40, projectionMatrix);

        data[56] = color.X;
        data[57] = color.Y;
        data[58] = color.Z;
        data[59] = color.W;

        data[60] = useTexture ? 1.0f : 0.0f;
        data[61] = depthOffset;
        data[62] = 0f;
        data[63] = 0f;

        context->UpdateSubresource((ID3D11Resource*)this.quadConstantBuffer, 0, null, data, 0, 0);
    }

    private void UpdateProbeConstants(ID3D11DeviceContext* context)
    {
        var data = stackalloc float[QuadConstantFloats];
        WritePoint(data, 0, new Vector3(-0.75f, 0.55f, 0.1f));
        WritePoint(data, 4, new Vector3(0.75f, 0.55f, 0.1f));
        WritePoint(data, 8, new Vector3(0.75f, -0.55f, 0.1f));
        WritePoint(data, 12, new Vector3(-0.75f, 0.55f, 0.1f));
        WritePoint(data, 16, new Vector3(0.75f, -0.55f, 0.1f));
        WritePoint(data, 20, new Vector3(-0.75f, -0.55f, 0.1f));
        WriteMatrix(data, 24, Matrix4x4.Identity);
        WriteMatrix(data, 40, Matrix4x4.Identity);

        data[56] = 1.0f;
        data[57] = 0.0f;
        data[58] = 0.85f;
        data[59] = 0.85f;

        data[60] = 0f;
        data[61] = 0f;
        data[62] = 0f;
        data[63] = 0f;

        context->UpdateSubresource((ID3D11Resource*)this.quadConstantBuffer, 0, null, data, 0, 0);
    }

    private static void WritePoint(float* data, int offset, Vector3 point)
    {
        data[offset] = point.X;
        data[offset + 1] = point.Y;
        data[offset + 2] = point.Z;
        data[offset + 3] = 1f;
    }

    private static void WriteMatrix(float* data, int offset, Matrix4x4 matrix)
    {
        data[offset] = matrix.M11;
        data[offset + 1] = matrix.M12;
        data[offset + 2] = matrix.M13;
        data[offset + 3] = matrix.M14;
        data[offset + 4] = matrix.M21;
        data[offset + 5] = matrix.M22;
        data[offset + 6] = matrix.M23;
        data[offset + 7] = matrix.M24;
        data[offset + 8] = matrix.M31;
        data[offset + 9] = matrix.M32;
        data[offset + 10] = matrix.M33;
        data[offset + 11] = matrix.M34;
        data[offset + 12] = matrix.M41;
        data[offset + 13] = matrix.M42;
        data[offset + 14] = matrix.M43;
        data[offset + 15] = matrix.M44;
    }

    private static bool UsesReversedDepth(Matrix4x4 projectionMatrix)
    {
        // D3D standard perspective projections usually carry a negative M43;
        // reversed-Z projections usually carry a positive one. Use the actual
        // camera matrix so the TV does not draw over actors if the game/backend
        // hands us a standard depth buffer.
        return projectionMatrix.M43 > 0f;
    }

    private static bool CompileShader(string source, string entryPoint, string profile, ID3DBlob** blob, ID3DBlob** errors)
    {
        var sourceBytes = Encoding.UTF8.GetBytes(source);
        var entryBytes = Encoding.ASCII.GetBytes(entryPoint + "\0");
        var profileBytes = Encoding.ASCII.GetBytes(profile + "\0");

        fixed (byte* sourcePtr = sourceBytes)
        fixed (byte* entryPtr = entryBytes)
        fixed (byte* profilePtr = profileBytes)
        {
            var hr = D3DCompile(
                sourcePtr,
                (nuint)sourceBytes.Length,
                null,
                null,
                null,
                (sbyte*)entryPtr,
                (sbyte*)profilePtr,
                0,
                0,
                blob,
                errors);
            return !Failed(hr);
        }
    }

    private static string ReadBlobText(ID3DBlob* blob)
    {
        if (blob is null)
        {
            return "Shader compilation failed without an error blob.";
        }

        var length = checked((int)blob->GetBufferSize());
        return Marshal.PtrToStringAnsi((nint)blob->GetBufferPointer(), length) ?? "Shader compilation failed.";
    }

    private static bool Failed(HRESULT hr)
    {
        return hr.Value < 0;
    }

    private static void Release<T>(T* ptr)
        where T : unmanaged
    {
        if (ptr is not null)
        {
            ((IUnknown*)ptr)->Release();
        }
    }

    private const string VertexShaderSource = """
        cbuffer ScreenQuad : register(b0)
        {
            float4 positions[6];
            row_major float4x4 view;
            row_major float4x4 projection;
            float4 color;
            float4 flags;
        };

        struct VSOut
        {
            float4 pos : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        VSOut main(uint id : SV_VertexID)
        {
            float2 uvs[6] =
            {
                float2(0, 0), float2(1, 0), float2(1, 1),
                float2(0, 0), float2(1, 1), float2(0, 1)
            };

            VSOut output;
            float4 viewPos = mul(positions[id], view);
            viewPos.z += flags.y;
            output.pos = mul(viewPos, projection);
            output.uv = uvs[id];
            return output;
        }
        """;

    private const string PixelShaderSource = """
        cbuffer ScreenQuad : register(b0)
        {
            float4 positions[6];
            row_major float4x4 view;
            row_major float4x4 projection;
            float4 color;
            float4 flags;
        };

        Texture2D screenTexture : register(t0);
        SamplerState screenSampler : register(s0);

        float4 main(float4 pos : SV_POSITION, float2 uv : TEXCOORD0) : SV_TARGET
        {
            if (flags.x > 0.5)
            {
                float3 rgb = screenTexture.Sample(screenSampler, uv).rgb;
                return float4(rgb, color.a);
            }

            return color;
        }
        """;

    private readonly record struct NativeQuad(bool Enabled, Vector3 TopLeft, Vector3 TopRight, Vector3 BottomRight, Vector3 BottomLeft);
}

public readonly record struct NativeDecorQuad(
    Vector3 TopLeft,
    Vector3 TopRight,
    Vector3 BottomRight,
    Vector3 BottomLeft,
    Vector4 Color,
    float DepthOffset);
