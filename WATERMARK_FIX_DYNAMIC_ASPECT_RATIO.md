# Watermark Fix v4 - Dynamic Aspect Ratio Scaling

## Problem Identified

The previous fix worked for 16:9 landscape videos but failed for 9:16 portrait videos:

**Landscape (1920x1080):**
- Canvas factor: 0.443 (850px / 1920px)
- Display at 40% of canvas = 340px
- Rendered at: 0.40 * 0.443 = 17.7% of video ? Correct visual size

**Portrait (1080x1920):**
- Canvas factor: 0.260 (281px / 1080px) 
- Display at 40% of canvas = 112px
- Rendered at: 0.40 * 0.260 = **10.4% of video** ? Too small!

The issue: **Fixed 40% display percentage doesn't account for canvas width ratio differences**

## Solution: Dynamic Display Percentage

Instead of using a fixed 40% display, calculate a **dynamic percentage** that ensures consistent visual rendering across all video aspect ratios.

### Formula

```
Target Render Size: 17% of video width (consistent across all videos)
Dynamic Display %: Target / CanvasToVideoFactor
Final Render %: Dynamic Display % * Canvas Factor * Slider
```

### Examples

**Landscape (1920x1080):**
- Canvas factor: 0.443
- Dynamic display: 17% / 0.443 = 38% of canvas
- Render: 38% * 0.443 * 1.0 = 17% of video ?

**Portrait (1080x1920):**
- Canvas factor: 0.260
- Dynamic display: 17% / 0.260 = 65% of canvas (clamped to 60%)
- Render: 60% * 0.260 * 1.0 = 15.6% of video ? (same visual size!)

## Key Changes

1. **Logo Sizing** (`BtnOpenWatermarkEditor_Click`):
   - Calculate `dynamicDisplayPercent = targetRenderPercent / _editorPreviewScale`
   - Clamp to 20-60% range for reasonable display
   - Use dynamic % instead of fixed 40%

2. **Scale Calculation** (`BtnSaveWatermark_Click`):
   - Use same dynamic calculation
   - Formula remains: `dynamicDisplay * canvasFactor * slider`
   - Now produces consistent 17% render size across aspect ratios

## Visual Impact

- **Same visual size** across landscape and portrait videos
- **Proper positioning** maintained (Y position was already correct)
- **Larger watermark** for portrait videos (65% of canvas instead of 40%)
- **Better WYSIWYG** preview in editor

## Testing

For 9:16 portrait video (1080x1920):
```
Canvas factor: 0.260 (281 / 1080)
Dynamic display: 17% / 0.260 = 65% ? clamped to 60%
Logo display: 281 * 0.60 = 168px
Expected render: 0.60 * 0.260 * 1.0 = 0.156 (?15.6% of video)
Video pixels: 1080 * 0.156 ? 169px (matches!)
```

This ensures the watermark appears at roughly the same visual size in both landscape and portrait videos.
