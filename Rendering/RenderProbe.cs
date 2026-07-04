using System;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

using GameCameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;
using KernelDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;
using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;
using RenderTargetManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager;
using SceneCameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager;

namespace VideoSyncPrototype.Rendering;

public static unsafe class RenderProbe
{
    public static string Capture()
    {
        var output = new StringBuilder();
        output.AppendLine("Real renderer probe");

        try
        {
            var device = KernelDevice.Instance();
            output.AppendLine($"Kernel device: {Ptr(device)}");
            if (device is not null)
            {
                output.AppendLine($"Resolution: {device->Width}x{device->Height}, feature level: 0x{device->D3DFeatureLevel:X}");
                output.AppendLine($"D3D11 context: {Ptr(device->D3D11DeviceContext)}");
                output.AppendLine($"Swap chain: {Ptr(device->SwapChain)}");
                output.AppendLine($"Immediate context: {Ptr(device->ImmediateContext)}");
            }

            var renderTargets = RenderTargetManager.Instance();
            output.AppendLine($"Render targets: {Ptr(renderTargets)}");
            if (renderTargets is not null)
            {
                output.AppendLine($"Scene size: {renderTargets->Resolution_Width}x{renderTargets->Resolution_Height}");
                AppendTexture(output, "Depth stencil", renderTargets->DepthStencil);
                AppendTexture(output, "Back buffer", renderTargets->SwapChainBackBuffer);
                AppendTexture(output, "Swap depth", renderTargets->SwapChainDepthStencil);
                AppendTexture(output, "GBuffer 0", renderTargets->GBuffers.Length > 0 ? renderTargets->GBuffers[0].Value : null);
            }

            var gameCameras = GameCameraManager.Instance();
            output.AppendLine($"Game camera manager: {Ptr(gameCameras)}");
            if (gameCameras is not null)
            {
                var activeCamera = gameCameras->GetActiveCamera();
                output.AppendLine($"Active game camera: {Ptr(activeCamera)}");
                output.AppendLine($"Active camera index: {gameCameras->ActiveCameraIndex}");
                if (activeCamera is not null)
                {
                    output.AppendLine($"FoV: {activeCamera->FoV:0.000}, distance: {activeCamera->Distance:0.000}");
                }
            }

            var sceneCameras = SceneCameraManager.Instance();
            output.AppendLine($"Scene camera manager: {Ptr(sceneCameras)}");
            if (sceneCameras is not null)
            {
                output.AppendLine($"Current scene camera: {Ptr(sceneCameras->CurrentCamera)}");
                output.AppendLine($"Scene camera index: {sceneCameras->CameraIndex}");
            }
        }
        catch (Exception ex)
        {
            output.AppendLine($"Probe failed: {ex.GetType().Name}: {ex.Message}");
            Plugin.Log.Warning(ex, "Real renderer probe failed.");
        }

        output.AppendLine();
        output.AppendLine("If these pointers are live in-game, the next step is a D3D11 draw hook that writes a textured quad against the scene depth buffer instead of ImGui.");
        return output.ToString();
    }

    private static void AppendTexture(StringBuilder output, string label, KernelTexture* texture)
    {
        output.AppendLine($"{label}: {Ptr(texture)}");
        if (texture is null)
        {
            return;
        }

        output.AppendLine($"  size: {texture->ActualWidth}x{texture->ActualHeight}, format: {texture->TextureFormat}, flags: {texture->Flags}");
        output.AppendLine($"  D3D11 texture: {Ptr(texture->D3D11Texture2D)}");
        output.AppendLine($"  shader resource view: {Ptr(texture->D3D11ShaderResourceView)}");
    }

    private static string Ptr(void* ptr)
    {
        return ptr is null ? "null" : $"0x{(nuint)ptr:X}";
    }
}
