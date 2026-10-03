# TITAN ENGINE - CUSTOM RESOLUTION IMPLEMENTATION

## Overview
Complete implementation of custom resolution support in TitanEngine V94 with:
- Resolution preset selection (Original, 1920x1080 HD, 1080x1920 Video d?c, 1280x720, Custom)
- Custom resolution input with validation
- FFmpeg scale filter with proper watermark integration
- Automatic dimension validation (even numbers only for H.264)

---

## Changes Made

### 1. **Data Model (RenderJob Class)**

**Location:** `MainWindow.xaml.cs`, lines 51-56

**Added Properties:**
```csharp
public int TargetWidth { get; set; } = -1;   // -1 = Original, >0 = Custom width
public int TargetHeight { get; set; } = -1;  // -1 = Original, >0 = Custom height
```

**Purpose:**
- Store parsed resolution values
- `-1` indicates "use original resolution"
- Values > 0 indicate explicit target dimensions (always even numbers)

---

### 2. **UI Event Handler (CmbRes_SelectionChanged)**

**Location:** `MainWindow.xaml.cs`, around line 1750

**Logic:**
```csharp
private void CmbRes_SelectionChanged(object sender, SelectionChangedEventArgs e)
{
    if (cmbRes.SelectedItem is ComboBoxItem item)
    {
        string content = item.Content?.ToString() ?? "";
        if (content.Contains("Custom"))
        {
            gridCustomRes.Visibility = Visibility.Visible;
            LogSystem("[RESOLUTION] Custom resolution mode enabled");
        }
        else
        {
            gridCustomRes.Visibility = Visibility.Collapsed;
            LogSystem($"[RESOLUTION] Selected: {content}");
        }
    }
}
```

**Behavior:**
- When user selects "Custom (Nh?p s?)" ? Shows width/height input fields
- When user selects any preset ? Hides width/height input fields
- Logs user selection for debugging

---

### 3. **Resolution Parsing Helper (ParseResolution)**

**Location:** `MainWindow.xaml.cs`, after LogSystem() method

**Signature:**
```csharp
private (int Width, int Height) ParseResolution(string resolutionText)
```

**Logic:**

| Input | Output | Notes |
|-------|--------|-------|
| "Original" | (-1, -1) | Keep source resolution |
| "1920x1080 (HD)" | (1920, 1080) | Extract from preset string |
| "Custom (Nh?p s?)" | Parse txtResW/txtResH | Read custom input textboxes |
| Invalid input | (-1, -1) | Fallback to original |

**Validation:**
- Ensures all dimensions are **even numbers** (FFmpeg H.264 requirement)
- If odd, automatically increments by 1
- Example: 1919x1081 ? 1920x1082
- Logs all parsing operations

**Example Calls:**
```csharp
ParseResolution("Original")                    // ? (-1, -1)
ParseResolution("1920x1080 (HD)")             // ? (1920, 1080)
ParseResolution("1080x1920 ( Video d?c )")   // ? (1080, 1920)
ParseResolution("1280x720")                   // ? (1280, 720)
```

---

### 4. **Job Creation (BtnAddJob_Click)**

**Location:** `MainWindow.xaml.cs`, around line 1837-1850

**Code Changes:**
```csharp
// BEFORE:
Resolution = (cmbRes.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Original",

// AFTER:
string resolutionText = (cmbRes.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Original";
(int targetW, int targetH) = ParseResolution(resolutionText);

// In job initialization:
Resolution = resolutionText,
TargetWidth = targetW,
TargetHeight = targetH,
```

**Additional Logging:**
```csharp
if (job.TargetWidth > 0 && job.TargetHeight > 0)
    LogSystem($"[RESOLUTION] Target: {job.TargetWidth}x{job.TargetHeight}");
```

---

### 5. **FFmpeg Scale Filter (ExecuteRenderAsync)**

**Location:** `MainWindow.xaml.cs`, around line 354-366

**Previous Implementation:**
```csharp
if (job.Resolution != "Original")
{
    videoFilters.Add($"scale=-2:{job.Resolution.Replace("p", "")}:flags=lanczos");
}
```

**New Implementation:**
```csharp
if (job.Resolution != "Original" && (job.TargetWidth > 0 && job.TargetHeight > 0))
{
    // Custom resolution: Use explicit width and height
    videoFilters.Add($"scale={job.TargetWidth}:{job.TargetHeight}:flags=lanczos");
    onLog($"[SCALE] {job.TargetWidth}x{job.TargetHeight} (custom resolution)");
}
else if (job.Resolution != "Original")
{
    // Preset resolution: Extract height from string like "1920x1080 (HD)"
    string heightStr = job.Resolution.Split('x').LastOrDefault()?.Split(' ').FirstOrDefault() ?? "";
    if (!string.IsNullOrEmpty(heightStr) && heightStr.Replace("p", "").All(char.IsDigit))
    {
        videoFilters.Add($"scale=-2:{heightStr.Replace("p", "")}:flags=lanczos");
        onLog($"[SCALE] Preset resolution selected: {job.Resolution}");
    }
}
```

**Scale Filter Options:**

| Scenario | Filter | Notes |
|----------|--------|-------|
| Original resolution | (skipped) | No scale filter applied |
| Custom preset (e.g., 1920x1080) | `scale=-2:1080:flags=lanczos` | -2 = maintain aspect ratio |
| Custom custom size (e.g., 1080x1920) | `scale=1080:1920:flags=lanczos` | Exact dimensions |

**Watermark Integration:**
- Scale filter runs **before** watermark overlay
- Watermark automatically scales relative to new video dimensions
- `scale2ref` ensures watermark positioning is correct on resized video

---

## XAML UI Elements

**Location:** `MainWindow.xaml`, lines 311-330

```xaml
<!-- Resolution ComboBox -->
<ComboBox x:Name="cmbRes" SelectionChanged="CmbRes_SelectionChanged">
    <ComboBoxItem Content="Original" IsSelected="True"/>
    <ComboBoxItem Content="1920x1080 (HD)"/>
    <ComboBoxItem Content="1080x1920 ( Video d?c )"/>
    <ComboBoxItem Content="1280x720"/>
    <ComboBoxItem Content="Custom (Nh?p s?)"/>
</ComboBox>

<!-- Custom Resolution Inputs (initially hidden) -->
<Grid x:Name="gridCustomRes" Visibility="Collapsed" Margin="0,5,0,0">
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="*"/>
        <ColumnDefinition Width="20"/>
        <ColumnDefinition Width="*"/>
    </Grid.ColumnDefinitions>
    <TextBox x:Name="txtResW" Grid.Column="0" Text="1080" ToolTip="Width"/>
    <TextBlock Text="x" Grid.Column="1" HorizontalAlignment="Center" VerticalAlignment="Center" Foreground="#888"/>
    <TextBox x:Name="txtResH" Grid.Column="2" Text="1920" ToolTip="Height"/>
</Grid>
```

---

## Usage Flow

### Step 1: User Interface Selection
```
User opens EXPORT section ? Selects from cmbRes dropdown
```

### Step 2: Conditional UI Display
```
If "Custom (Nh?p s?)" selected
  ?? gridCustomRes.Visibility = VISIBLE
  ?? txtResW & txtResH enabled for input
  ?? Log: "[RESOLUTION] Custom resolution mode enabled"

Else (any preset)
  ?? gridCustomRes.Visibility = COLLAPSED
  ?? Log: "[RESOLUTION] Selected: {preset name}"
```

### Step 3: Job Queue Addition (btnAddJob_Click)
```
Resolution text ? ParseResolution() ? (Width, Height)
  ?? "Original" ? (-1, -1)
  ?? "1920x1080 (HD)" ? (1920, 1080)
  ?? "Custom" ? Parse txtResW/txtResH, validate even dimensions
  ?? Invalid ? (-1, -1), log error

Job.TargetWidth = Width
Job.TargetHeight = Height
```

### Step 4: FFmpeg Rendering (ExecuteRenderAsync)
```
If TargetWidth > 0 && TargetHeight > 0
  ?? Apply: scale={Width}:{Height}:flags=lanczos
  ?? Log: "[SCALE] {Width}x{Height} (custom resolution)"

Else if preset selected
  ?? Extract height from preset string
  ?? Apply: scale=-2:{Height}:flags=lanczos (maintain aspect)
  ?? Log: "[SCALE] Preset resolution selected"

Else (Original)
  ?? Skip scale filter
```

---

## Validation & Safety

### Dimension Validation

**Rule 1: Must be positive integers**
```
Width > 0 && Height > 0
```

**Rule 2: Must be even numbers (H.264 requirement)**
```csharp
if (width % 2 != 0) width++;
if (height % 2 != 0) height++;
```

**Rule 3: Parse error fallback**
```csharp
Invalid input ? (-1, -1) ? Use original resolution
```

### Examples

| User Input | After Validation | Reason |
|------------|------------------|--------|
| 1920 x 1080 | 1920 x 1080 | Already even |
| 1919 x 1081 | 1920 x 1082 | Incremented to even |
| 0 x 1080 | Use Original | Width invalid |
| invalid x text | Use Original | Parse failed |

---

## Logging Output

When user adds a job with custom resolution:

```
[14:23:45] [RESOLUTION] Custom resolution mode enabled
[14:23:50] [RESOLUTION] Selected: Custom (Nh?p s?)
[14:23:52] [RESOLUTION] Parsed: 1080x1920 -> 1080x1920
[14:23:52] [ANALYZING] 1/1: MyVideo.mp4
[14:23:52] [RESOLUTION] Target: 1080x1920
[14:23:53] [QUEUED] Job #1 - Watermark Aspect Ratio: 1.000:1
[14:24:10] [SCALE] 1080x1920 (custom resolution)
```

---

## Troubleshooting

### Issue: Custom resolution textboxes don't appear
**Solution:**
- Verify `cmbRes.SelectedItem` contains "Custom"
- Check `gridCustomRes` exists in XAML with `x:Name="gridCustomRes"`
- Ensure `Visibility="Collapsed"` in XAML (not `Hidden`)

### Issue: Resolution not being applied
**Solution:**
- Check that `txtResW.Text` and `txtResH.Text` contain valid numbers
- Verify no parsing errors in logs
- Ensure TargetWidth/TargetHeight are > 0 in job

### Issue: Output video has black bars or incorrect aspect ratio
**Solution:**
- Use `-2` in preset resolutions to maintain aspect ratio
- For custom resolutions, user should calculate correct dimensions
- Example: If source is 1920x1080, choosing 1920x1920 will stretch

### Issue: FFmpeg error "Invalid width/height"
**Solution:**
- All dimensions must be even (?2)
- ParseResolution() handles this automatically
- Check logs for dimension values before render

---

## Technical Notes

### Why Even Numbers?
H.264 video codec works with 2x2 pixel blocks. Dimensions must be divisible by 2.
If user enters odd number ? automatically rounded up to nearest even.

### Why -1 for Original?
Sentinel value convention. Makes it easy to detect "use original resolution" vs invalid input.

### Filter Chain Integration
```
Original FFmpeg command:
  Input Video ? Scale Filter ? Color Grading ? FX ? Watermark ? Encode

With Watermark:
  [Input 0] ? Scale to TargetWidth:TargetHeight
  [Input 1] (Watermark) ? Scale2ref to match video dimensions
  Overlay watermark on scaled video
```

### Aspect Ratio Considerations
- Preset resolutions preserve aspect ratio using `-2` flag in scale filter
- Custom resolutions use explicit width/height (no aspect ratio preservation)
- Users should input correct dimensions for desired result

---

## Files Modified

1. **TitanEngine/MainWindow.xaml.cs**
   - Added TargetWidth, TargetHeight to RenderJob
   - Updated CmbRes_SelectionChanged
   - Added ParseResolution() method
   - Updated BtnAddJob_Click
   - Updated ExecuteRenderAsync scale filter logic

2. **TitanEngine/MainWindow.xaml** (no changes needed - already correct)
   - cmbRes with presets already present
   - gridCustomRes with txtResW, txtResH already present

---

## Testing Checklist

- [ ] Select "Original" ? No scale filter applied
- [ ] Select "1920x1080 (HD)" ? Scale to 1920x1080
- [ ] Select "1080x1920" ? Scale to 1080x1920
- [ ] Select "1280x720" ? Scale to 1280x720
- [ ] Select "Custom" ? gridCustomRes becomes visible
- [ ] Enter custom dimensions ? Job queued with TargetWidth/TargetHeight
- [ ] Enter odd dimensions (1919x1081) ? Converted to 1920x1082
- [ ] With watermark ? Watermark scales correctly with video
- [ ] Check logs ? All operations logged correctly

---

## API Reference

### ParseResolution Method

```csharp
/// <summary>
/// Parse resolution string and extract target width and height.
/// Validates that dimensions are even numbers (FFmpeg requirement).
/// </summary>
/// <param name="resolutionText">
/// Resolution string from cmbRes ComboBox
/// Expected formats:
///   - "Original"
///   - "1920x1080 (HD)"
///   - "1080x1920 ( Video d?c )"
///   - "Custom (Nh?p s?)" (reads from txtResW/txtResH)
/// </param>
/// <returns>
/// Tuple of (Width, Height)
/// - (-1, -1): Use original resolution
/// - (W, H): Use custom resolution (W, H always even)
/// </returns>
private (int Width, int Height) ParseResolution(string resolutionText)
```

---

## Version History

- **V94.0 (Current)**
  - ? Custom resolution input
  - ? Resolution preset selection
  - ? FFmpeg scale filter integration
  - ? Watermark compatibility
  - ? Dimension validation (even numbers)

---

**End of Documentation**
