# ?? UPSCAYL INTEGRATION GUIDE - SETUP & IMPLEMENTATION

## ?? Folder Structure Setup

Place all Upscayl files next to your TitanEngine.exe:

```
TitanEngine.exe
??? ffmpeg.exe
??? ffprobe.exe
??? upscayl/                    ? Create this folder
    ??? Upscayl.exe
    ??? locales/               ? Copy from Upscayl download
    ??? resources/             ? Copy from Upscayl download
    ??? chrome_100_percent.pak
    ??? chrome_200_percent.pak  
    ??? resources.pak
    ??? snapshot_blob.bin
    ??? LICENSE.electron.txt
    ??? libEGL.dll
    ??? libGLESv2.dll
    ??? ffmpeg.dll
    ??? d3dcompiler_47.dll
    ??? ... (all other .dll files)
```

### Steps to Setup:
1. **Download Upscayl**: [upscayl.github.io](https://upscayl.github.io/)
2. **Locate the portable version** (not installer)
3. **Copy entire `Upscayl` folder** to your TitanEngine application directory
4. **Verify**: `[AppDir]/upscayl/Upscayl.exe` should exist

---

##?? Code Implementation - Key Functions

### 1. Initialize Upscayl Path
```csharp
private static string? _upscaylPath;

public static void Initialize()
{
    // ... existing code ...
    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
    _upscaylPath = System.IO.Path.Combine(baseDir, "upscayl", "Upscayl.exe");
}
```

### 2. Execute Upscayl Upscaling
```csharp
public static async Task<bool> UpscaleWithUpscaylAsync(
    string inputPath, 
    string outputPath, 
    string mode,  // "2x" or "4x"
    Action<string> onLog, 
    CancellationToken token)
{
    // Check if Upscayl exists
    if (!File.Exists(_upscaylPath))
    {
        onLog($"[UPSCAYL] Not found at: {_upscaylPath}");
        return false;
    }

    // Determine scale
    string scale = mode.Contains("4x") ? "4" : "2";
    string model = "realesrgan";  // Best quality

    // Command: Upscayl.exe -i input.mp4 -o output.mp4 -s 2 -m realesrgan
    var psi = new ProcessStartInfo
    {
        FileName = _upscaylPath,
        Arguments = $"-i \"{inputPath}\" -o \"{outputPath}\" -s {scale} -m {model}",
        UseShellExecute = false,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        CreateNoWindow = true,
        WorkingDirectory = Path.GetDirectoryName(_upscaylPath)
    };

    using (var process = new Process { StartInfo = psi })
    {
        process.OutputDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                onLog($"[UPSCAYL-LOG] {e.Data}");
        };

        process.Start();
        process.BeginOutputReadLine();

        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (TaskCanceledException)
        {
            try { process.Kill(); } catch { }
            return false;
        }

        return process.ExitCode == 0 && File.Exists(outputPath);
    }
}
```

### 3. Integrate into ExecuteRenderAsync

In `ExecuteRenderAsync`, before FFmpeg encoding:

```csharp
// [NEW] Step 1: UPSCALE if needed
string videoToEncode = job.SourcePath;

if (job.UpscaleMode != "Off")
{
    onLog($"[PREPROCESSING] Upscaling with Upscayl...");
    
    string upscaledPath = Path.Combine(outDir, 
        $"{Path.GetFileNameWithoutExtension(rawName)}_upscaled.mp4");
    
    bool success = await UpscaleWithUpscaylAsync(
        job.SourcePath, 
        upscaledPath, 
        job.UpscaleMode, 
        onLog, 
        token);
    
    if (success && File.Exists(upscaledPath))
    {
        videoToEncode = upscaledPath;
        onLog($"[PREPROCESSING] Upscale complete!");
    }
    else
    {
        onLog($"[PREPROCESSING] Upscale failed, using original");
    }
}

// [MODIFIED] Step 2: Use upscaled video for encoding
// Replace: cmd.Append($"-y -i \"{job.SourcePath}\" ");
// With:    cmd.Append($"-y -i \"{videoToEncode}\" ");
```

### 4. Skip FFmpeg Scaling if Upscayl Was Used

```csharp
// When building filter chain:
if (videoToEncode == job.SourcePath && !job.Resolution.Contains("Original"))
{
    // Only add scaling if NOT using Upscayl
    // Build scale filters...
}
else if (videoToEncode != job.SourcePath)
{
    onLog($"[FILTER] Skipping scale - video already upscaled by Upscayl");
}
```

---

## ?? Configuration in UI

Update XAML to show Upscayl integration status:

```xaml
<!-- Add to MainWindow.xaml -->
<TextBlock Name="txtUpscaylStatus" Text="Upscayl: ? Ready" 
    Foreground="#00FF00" FontSize="9" Margin="0,5,0,0"/>
```

Update codebehind:

```csharp
private void OnSystemLoaded(object sender, RoutedEventArgs e)
{
    // ... existing code ...
    
    bool upscaylReady = File.Exists(
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, 
        "upscayl", "Upscayl.exe"));
    
    if (txtUpscaylStatus != null)
    {
        if (upscaylReady)
        {
            txtUpscaylStatus.Text = "Upscayl: ? Ready";
            txtUpscaylStatus.Foreground = Brushes.LimeGreen;
        }
        else
        {
            txtUpscaylStatus.Text = "Upscayl: ? Not Found";
            txtUpscaylStatus.Foreground = Brushes.Red;
        }
    }
}
```

---

## ?? Workflow

```
Input Video (576×1024)
    ?
User selects: "2x Upscale"
    ?
[PREPROCESSING] Call Upscayl.exe
    ?? Input: 576×1024
    ?? Command: Upscayl.exe -i input.mp4 -o upscaled.mp4 -s 2 -m realesrgan
    ?? Output: 1152×2048 (AI-upscaled)
    ?
[ENCODING] Call FFmpeg
    ?? Input: upscaled.mp4
    ?? Filters: FX, Color Grading, Watermark (NO scaling!)
    ?? Output: Final_1152x2048.mp4
```

---

## ?? Expected Performance

| Video | 2x Upscale | 4x Upscale |
|-------|-----------|-----------|
| 1 min | 2-3 min | 5-10 min |
| 5 min | 10-15 min | 25-50 min |
| 10 min | 20-30 min | 50-100 min |

*(Depends on GPU/CPU)*

---

## ? Testing Checklist

- [ ] Upscayl folder copied correctly
- [ ] `Upscayl.exe` exists at `[AppDir]/upscayl/Upscayl.exe`
- [ ] Code compiles without errors
- [ ] Test 2x upscale on small video
- [ ] Test 4x upscale on small video
- [ ] Output quality looks good
- [ ] Logs show `[UPSCAYL-SUCCESS]`

---

## ?? Troubleshooting

### "Upscayl: ? Not Found"
- Check folder path
- Verify `upscayl/Upscayl.exe` exists
- Copy all .dll files from Upscayl download

### "UPSCAYL-FAILED" in logs
- Check Upscayl version compatibility
- Verify input video codec is H.264
- Check disk space

### Slow performance
- GPU might not be detected
- Use CPU fallback if needed
- Reduce video resolution first

---

**Status**: Ready to implement ?

