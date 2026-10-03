# Watermark Fix - Complete Analysis & Solution

## Issues Identified

### Issue 1: Watermark Can't Move to Right Side
**Problem**: The watermark could only be positioned from 0% to ~77% of the canvas width, not the full 100%.

**Root Cause**: 
```csharp
// OLD CODE - Incorrect constraint
newLeft = Math.Max(0, Math.Min(newLeft, cvsWatermarkPreview.ActualWidth - imgWatermarkPreview.ActualWidth));
```

This constraint prevented the image's left edge from going beyond `canvas_width - image_width`. For an 850px canvas with a 384px watermark:
- Max left position: 850 - 384 = 466px
- Max center position: 466 + 192 = 658px  
- As percentage: 658/850 = **77.4%** (not 100%!)

**Solution**: Allow the image center to move across the full canvas by using half-width constraints:
```csharp
// NEW CODE - Allow full canvas movement
double imgHalfWidth = imgWatermarkPreview.ActualWidth / 2;
newLeft = Math.Max(-imgHalfWidth, Math.Min(newLeft, cvsWatermarkPreview.ActualWidth - imgHalfWidth));
```

Now the center can range from 0% to 100% of the canvas width!

---

### Issue 2: Rendered Watermark Smaller Than Editor Preview
**Problem**: The watermark appeared at 45% of canvas in editor, but rendered at only 20% of video.

**Root Cause - Inverted Scale Factor**:
```csharp
// OLD CODE - WRONG!
_editorPreviewScale = (videoWidth * 0.20) / canvasWidth;
// For 1920px video and 850px canvas: (1920 * 0.20) / 850 = 0.451

// This made editor display: 850 * 0.451 = 383px (45% of canvas!)
// But render was supposed to be: 1920 * 0.20 = 384px (20% of video)
```

**Why It's Wrong**:
- Canvas is a **scaled representation** of the video
- Canvas scaling factor: `canvasWidth / videoWidth` = `850 / 1920` = **0.443**
- A 20% video watermark in the editor should appear as: `20% * 0.443 = 8.86%` of canvas (too small!)
- But we were displaying it at `45%` of canvas (5x too large!)

**Solution - Apply Scale Correction**:
```csharp
// NEW CODE - CORRECT
_editorPreviewScale = canvasWidth / videoWidth;  // 0.443

// Display at 40% of canvas for visibility
const double editorDisplayPercent = 0.40;

// Apply correction when saving:
_watermarkScale = editorDisplayPercent * _editorPreviewScale * sliderScale;
// = 0.40 * 0.443 * 1.0 = 0.177 (?17.7% of video when slider at default)
```

This way:
- **Editor** shows watermark at **40% of canvas** (large and visible)
- **Canvas scale** factor is **0.443** (850px represents 1920px)
- **Render size** is corrected to **17.7% of video** (matches the actual render scale!)

---

### Issue 3: Position Not Accounting for Watermark Width
**Problem**: When overlay positioned watermark, it used the center position directly, but overlay's `x` parameter positions the **left edge**, not the center.

**Example**:
- Editor canvas: 850px
- Watermark centered at 77.4% of canvas (658px from left)
- Position stored: 77.4%
- Render: `x = 1920 * 0.774 = 1485px` (this is the LEFT edge position!)
- With 384px wide watermark, actual center at: `1485 + 192 = 1677px`
- Actual position: `1677/1920 = 87.4%` (not 77.4%!)

**Solution - Account for Watermark Width**:
```csharp
// NEW CODE - Adjust x to center based on watermark width
overlay=x=main_w*(wmkXPercent - wmkScale/2):y=main_h*wmkYPos

// Example:
// wmkXPercent = 0.774 (center at 77.4%)
// wmkScale = 0.20 (watermark is 20% of video)
// x = main_w * (0.774 - 0.20/2)
//   = main_w * (0.774 - 0.10)
//   = main_w * 0.674
//   = 1920 * 0.674 = 1294px (left edge)
// Center: 1294 + 192 = 1486px
// Position: 1486/1920 = 0.774 (? correct!)
```

---

## Summary of Changes

### In `BtnOpenWatermarkEditor_Click`:
1. Changed `_editorPreviewScale` calculation to use canvas-to-video ratio (0.443) instead of wrong formula
2. Display watermark at **40% of canvas** instead of trying to match render size (makes it visible for editing)
3. Log the scale factor for debugging

### In `ImgWatermark_MouseMove`:
1. Allow watermark to move **fully across canvas** (0-100%) instead of stopping at 77%
2. Use half-width constraints to permit center movement to canvas edges

### In `BtnSaveWatermark_Click`:
1. Apply **scale correction** when saving: `display_percent * canvas_scale * slider`
2. Position stored as percentage of full canvas width (0-100%)
3. Better logging to show the calculation steps

### In `ExecuteRenderAsync` (FFmpeg filter):
1. Adjust overlay `x` coordinate to account for watermark width
2. Formula: `x = main_w * (wmkXPercent - wmkScale/2)`
3. Log the final position formula for debugging

---

## Testing the Fix

### Test Case 1: Size Matching
1. Open editor with 1920x1080 video
2. Watermark appears at ~40% of the 850px canvas ? 340px displayed
3. Set slider to 1.0 (default)
4. Save and render
5. **Expected**: Watermark in final video is approximately **17.7% of video width**
6. **Visual check**: Watermark should appear proportionally similar to editor preview

### Test Case 2: Full Movement Range
1. Open watermark editor
2. Drag watermark all the way to the RIGHT edge of canvas
3. Save and render
4. **Expected**: Watermark should appear at the right side of final video (not cut off or repositioned)

### Test Case 3: Position Accuracy
1. Position watermark at center of editor canvas (50%)
2. Save and render
3. **Expected**: Watermark center in final video should be at ~50% horizontal position
4. Check that it's centered, not offset

---

## Technical Notes

- **Canvas aspect ratio**: Automatically adjusted to match video aspect ratio (landscape/portrait)
- **Scaling factor**: Always calculated as `canvasWidth / videoWidth` for consistency
- **Display size**: Fixed at 40% of canvas for comfortable editing (independent of video orientation)
- **Render size**: Calculated from display size × scaling factor × slider multiplier
- **Position range**: 0% to 100% of canvas (fully movable)
- **Overlay positioning**: Uses adjusted x coordinate to account for watermark center

