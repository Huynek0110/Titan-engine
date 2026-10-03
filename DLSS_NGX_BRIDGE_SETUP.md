# TITAN ENGINE - DLSS (NGX DLVSR) BRIDGE SETUP

## What this is
- This app now supports a **real NVIDIA NGX video super-resolution bridge**.
- Render flow for upscale:
  1) Try **NVIDIA NGX DLVSR**
  2) If not available/fails -> try **Upscayl**
  3) If still fails -> fallback to internal **DLSS/FSR-style FFmpeg pipeline**

## Backend paths
Titan Engine accepts either of these backends:

- `DLVSR.exe`
- `UpscalePipelineApp.exe` from NVIDIA Maxine VFX SDK Samples

Expected app folder:

`[AppDir]\dlss\`

For this project release build, that is usually one of:

- `TitanEngine\bin\Release\net8.0-windows\dlss\`
- `TitanEngine\bin\Any CPU\Release\net8.0-windows\dlss\`

## Usage
1. Run app and set hardware to `NVIDIA NVENC`.
2. In `AI UPSCALE ENGINE`, choose:
   - `2x Upscale (FSR + DLSS)` or
   - `4x Upscale (Ultra Quality)`
3. Start render.

## Auto Download / Auto Build
- When the user selects an upscale mode like `2x` or `4x`, if the NVIDIA backend is missing, app can offer:
  - auto-download the official public VFX sample source
  - auto-install CMake through `winget` if CMake is missing
  - auto-build `UpscalePipelineApp.exe` if the NVIDIA VFX SDK Core + `nvvfxupscale` feature are already installed
- If you choose `Yes`, app will automatically download the official VFX SDK sample bundle into:
  - `[AppDir]\dlss\NVIDIA_VFX_SDK_Samples.zip`
  - `[AppDir]\dlss\DLVSR_SETUP_README.txt`
  - `[AppDir]\dlss\NVIDIA_VFX_AUTO_SETUP.ps1`
- The folder will open automatically after download.
- App will also auto-try CMake build of `UpscalePipelineApp`.
- If the local machine does not yet have NVIDIA VFX SDK Core + `nvvfxupscale`, the helper script and README in the `dlss` folder guide the remaining steps.

## Why no DLVSR.exe after download?
- NVIDIA sample package does **not** contain prebuilt `DLVSR.exe`.
- Auto flow downloads source bundle first, then tries to build `UpscalePipelineApp.exe`.
- If machine does not have VFX SDK Core + `nvvfxupscale`, build cannot finish yet.
- That SDK Core and feature come from NVIDIA NGC, not from the public GitHub sample zip.

## NVIDIA requirement
- Official install guide:
  - https://docs.nvidia.com/maxine/vfx/latest/WindowsVFXSDK/InstalltheVFXSDK.html
- NGC collection:
  - https://catalog.ngc.nvidia.com/orgs/nvidia/teams/maxine/collections/maxine_windows_vfx_sdk_collection_ga
- Public sample source:
  - https://github.com/NVIDIA-Maxine/VFX-SDK-Samples

The app-generated helper script `NVIDIA_VFX_AUTO_SETUP.ps1` can finish the local build once:
- `VFXSDK_ROOT` is installed or detectable
- `nvvfxupscale` has been installed
- CMake and Visual Studio C++ tools are present

## Important for 8K outputs
- If output becomes very large (for example 4K source + `2x` => `7680x4320`), app now prefers:
  1. `hevc_nvenc` (if available), then
  2. automatic retry with `libx264` if GPU encoder fails.
- This avoids common `h264_nvenc` open-encoder errors on ultra-high resolutions.

## How scaling works
- `2x` -> runs NVIDIA backend at 2x if available.
- `4x` -> app chains two 2x passes when using the NVIDIA backend.

## Log signals
- NGX found:
  - `[DLSS-NGX] DLVSR bridge: READY`
- NGX run:
  - `[PREPROCESSING] Trying NVIDIA DLSS (NGX DLVSR)...`
  - `[DLSS-NGX] Success...`
- NGX missing:
  - `[DLSS-NGX] DLVSR.exe not found at: ...`
  - then app falls back automatically.
- Encoder auto-retry:
  - `[ENCODER-RETRY] Primary encoder '...' failed. Retrying with libx264...`
  - `[ENCODER-RETRY] CPU fallback succeeded.`

## Notes
- NGX DLVSR support depends on your NVIDIA driver/GPU/runtime.
- If NGX is missing or unsupported on a machine, the app still renders via fallback paths.
