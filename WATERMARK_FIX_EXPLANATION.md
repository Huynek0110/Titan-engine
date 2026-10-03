# WATERMARK SCALE BUG FIX

## Problem
Watermark was appearing **stretched/enlarged** (giãn to) in the final video output compared to what was shown in the visual editor.

## Root Cause
**Scale mismatch between editor preview and render engine:**

1. **Editor Phase** (Visual Editor):
   - Watermark image displayed at 30% of preview canvas width
   - Example: Canvas=850px ? Watermark display width = 850 × 0.3 = 255px
   - User adjusts slider (0.1 to 3.0) to scale this preview watermark

2. **Render Phase** (FFmpeg):
   - Code directly used slider value as video scale factor
   - Applied `scale2ref=w=rw*{sliderScale}` to actual video width
   - Example: Video=1920px, Slider=1.0 ? Watermark = 1920 × 1.0 = 1920px (HUGE!)
   
**Result**: Watermark was ~7.5x larger than intended (1920/255 ? 7.5)

## Solution
Track editor canvas dimensions and convert preview scale to actual video scale:

```
Preview watermark width:  canvas_width × 0.3 × slider_scale
Render watermark width:   video_width × actual_scale

To match:
actual_scale = (canvas_width / video_width) × 0.3 × slider_scale
```

### Changes Made:

1. **Added tracking fields** (MainWindow.xaml.cs):
   ```csharp
   private int _editorVideoWidth = 1920;
   private int _editorVideoHeight = 1080;
   private double _editorCanvasWidth = 850;
   private double _editorCanvasHeight = 500;
   private double _editorPreviewScale = 0.3;  // 30% of canvas width
   ```

2. **Store editor dimensions** in `BtnOpenWatermarkEditor_Click`:
   - When watermark editor opens, it detects actual video resolution
   - Calculates preview canvas size based on video aspect ratio
   - Stores these values for later scale conversion

3. **Convert scale on save** in `BtnSaveWatermark_Click`:
   ```csharp
   double previewToVideoRatio = _editorCanvasWidth / _editorVideoWidth;
   _watermarkScale = previewToVideoRatio * _editorPreviewScale * sliderScale;
   ```
   - `previewToVideoRatio`: How much preview is smaller than actual video
   - `_editorPreviewScale`: The 30% factor applied during preview
   - `sliderScale`: User's adjustment on the slider
   - Result: Correct scale for FFmpeg `scale2ref` filter

## Example Numbers:
- **Video Resolution**: 1920×1080
- **Preview Canvas**: 850×480 (matches aspect ratio)
- **Preview Scale**: 0.3 (30% of canvas = 255px on screen)
- **Slider Value**: 1.0

**Before Fix**:
- Render scale = 1.0
- FFmpeg: `scale2ref=w=rw*1.0` = 1920px ? (too large!)

**After Fix**:
- Ratio = 850/1920 = 0.4427
- Render scale = 0.4427 × 0.3 × 1.0 = 0.1328
- FFmpeg: `scale2ref=w=rw*0.1328` = 255px ? (matches preview!)

## Result
Now the watermark in the visual editor will match exactly what appears in the final rendered video.
