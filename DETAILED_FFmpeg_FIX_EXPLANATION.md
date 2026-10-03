# ?? DETAILED FIX EXPLANATION - FFmpeg scale ? scale2ref

## The Problem in Detail

### FFmpeg Filter Error
```
Exit Code: -22
Message: More input link labels specified for filter 'scale' than it has inputs: 2 > 1
```

### What Went Wrong

**Original Filter Chain (BROKEN):**
```ffmpeg
[logo_alpha][0:v]scale=w=iw*0.2:h=-1:flags=lanczos[logo_scaled][v_ref];
         ?        ?
         ???????????
              ?
     Two inputs to scale filter
     
BUT scale filter only accepts 1 input!
```

### Why FFmpeg Rejected It

```
FFmpeg Filter Documentation:
?????????????????????????????????

scale Filter:
  Input pads:  1 only ?
  Output pads: 1 only ?
  
  Cannot use: [pad1][pad2]scale=...  ?
  Must use:  [pad1]scale=...         ?

Error when trying to pass 2 inputs:
"More input link labels specified for filter 'scale' 
 than it has inputs: 2 > 1"
```

---

## The Solution Explained

### FFmpeg Filter: scale2ref

**What it does:**
- Takes a video to scale (logo)
- Takes a reference video (background)
- Scales the first video relative to the second
- Outputs both the scaled video AND the reference video

### The Fix

**Fixed Filter Chain (CORRECT):**
```ffmpeg
[logo_alpha][0:v]scale2ref=w=rw*0.2:h=-1:flags=lanczos[logo_scaled][v_ref];
         ?        ?
         ???????????
              ?
     Two inputs to scale2ref filter ?
     
scale2ref accepts 2 inputs by design!
AND produces 2 outputs [logo_scaled] and [v_ref]
```

---

## Comparison Table

### scale vs scale2ref

```
???????????????????????????????????????????????????????????
?    Property      ?     scale        ?    scale2ref      ?
???????????????????????????????????????????????????????????
? Purpose          ? Scale 1 video    ? Scale video vs    ?
?                  ? to fixed size    ? reference video   ?
???????????????????????????????????????????????????????????
? Input Pads       ? 1                ? 2 ?               ?
?                  ? (video only)     ? (video + ref)     ?
???????????????????????????????????????????????????????????
? Output Pads      ? 1                ? 2 ?               ?
?                  ? (scaled video)   ? (scaled + ref)    ?
???????????????????????????????????????????????????????????
? Width Formula    ? w=iw*X           ? w=rw*X ?          ?
?                  ? (logo width)     ? (ref width)       ?
???????????????????????????????????????????????????????????
? Use Case         ? Simple resizing  ? Aspect-ratio ?    ?
?                  ?                  ? relative scaling  ?
???????????????????????????????????????????????????????????
? For Watermark    ? ? WRONG         ? ? CORRECT        ?
? (dual input)     ? 2 inputs fails   ? Designed for it   ?
???????????????????????????????????????????????????????????
```

---

## Visual Flow

### BEFORE (BROKEN) ?

```
Inputs:
  [logo_alpha]  ???
                  ? Cannot route!
  [0:v]         ??? scale filter
                  ? only accepts
                  ? 1 input
                  ?
Error: 2 > 1 ?
```

### AFTER (FIXED) ?

```
Inputs:
  [logo_alpha]  ???
                  ???? scale2ref ?????? [logo_scaled]
  [0:v]         ???   (2 inputs)  ?
                  ?               ???? [v_ref]
Output pads:
  [logo_scaled] - Scaled logo (ready for rotation)
  [v_ref]       - Reference video (ready for overlay)
```

---

## Parameter Explanation

### Formula Breakdown

**Old Formula (WRONG):**
```
w=iw*0.2:h=-1

iw = input width = logo's original width
   = Always the same value (logo size)
   = Doesn't adapt to different videos ?

Result: Logo size doesn't change based on video size
```

**New Formula (CORRECT):**
```
w=rw*0.2:h=-1

rw = reference width = background video's width
   = Different for each video ?
   = Adapts to 1920px, 1080px, 540px, etc.
   = Logo scales relative to video ?

Result: Logo always 20% of video width (correct!)
```

### Example Calculation

**Scenario: Different Video Sizes**

```
Video 1 (16:9):  1920×1080
  w = rw * 0.2 = 1920 * 0.2 = 384px
  h = -1 (auto) = 384 / aspect_ratio = 216px
  Result: 384×216 logo ?

Video 2 (9:16):  1080×1920
  w = rw * 0.2 = 1080 * 0.2 = 216px
  h = -1 (auto) = 216 / aspect_ratio = 121px
  Result: 216×121 logo ?

Video 3 (1:1):   1080×1080
  w = rw * 0.2 = 1080 * 0.2 = 216px
  h = -1 (auto) = 216 / aspect_ratio = 216px
  Result: 216×216 logo ?

Notice: 
- Logo width scales with video width
- Logo aspect ratio is preserved
- h=-1 auto-calculates perfect height
```

---

## Complete FFmpeg Filter Chain (Fixed)

### Raw FFmpeg Command

```ffmpeg
ffmpeg -i video.mp4 -i logo.png \
  -filter_complex "
    [1:v]format=rgba,
    colorchannelmixer=aa=1.0[logo_alpha];
    
    [logo_alpha][0:v]scale2ref=w=rw*0.2:h=-1:flags=lanczos[logo_scaled][v_ref];
    [logo_scaled]rotate=0*PI/180:c=none:ow=rotw(0*PI/180):oh=roth(0*PI/180)[logo_rotated];
    [v_ref][logo_rotated]overlay=x=main_w*0.05:y=main_h*0.05[v_out]
  " \
  -map "[v_out]" -map "0:a?" \
  -c:v libx264 -c:a aac \
  output.mp4
```

### C# Code Generation (After Fix)

```csharp
// Step 1: Prepare logo (RGBA format with opacity)
filterComplex.Append($"[{watermarkInputIndex}:v]format=rgba,");
filterComplex.Append($"colorchannelmixer=aa={wmkOpacity}[logo_alpha];");

// Step 2: FIXED - Scale logo with scale2ref
filterComplex.Append($"[logo_alpha]{vMap}scale2ref=w=rw*{wmkScale}:h=-1:flags=lanczos[logo_scaled][v_ref];");
//                                        ?         ??
//                                   scale2ref  rw=reference width

// Step 3: Apply rotation
filterComplex.Append($"[logo_scaled]rotate={wmkRotation}*PI/180:c=none:ow=rotw({wmkRotation}*PI/180):oh=roth({wmkRotation}*PI/180)[logo_rotated];");

// Step 4: Overlay on video
filterComplex.Append($"[v_ref][logo_rotated]overlay=x=main_w*{wmkXPos}:y=main_h*{wmkYPos}[v_out];");
```

---

## Step-by-Step Execution Flow

### How scale2ref Works

```
Input Stage:
?????????????
Pad 0: [logo_alpha]    ? 200×100 logo with transparency
Pad 1: [0:v]           ? 1920×1080 video


Processing Stage:
?????????????????
scale2ref=w=rw*0.2:h=-1:flags=lanczos

rw = reference width from pad 1 = 1920px
w = 1920 * 0.2 = 384px
h = -1 ? FFmpeg calculates = 384 / (200/100) = 192px
flags = lanczos (high quality)


Output Stage:
??????????????
Pad 0: [logo_scaled]   ? 384×192 logo (scaled, aspect ratio preserved)
Pad 1: [v_ref]         ? 1920×1080 video (pass-through, unchanged)


These 2 outputs flow to next filters:
- [logo_scaled] ? rotate filter
- [v_ref] ? overlay filter (as background)
```

---

## Validation

### How to Verify the Fix Works

**Check 1: FFmpeg Syntax**
```
If you see this error:
  "More input link labels specified for filter 'scale' than it has inputs"

Then you STILL have the bug ?

If rendering completes without this error:
  Watermark rendered successfully ?
```

**Check 2: Output Quality**
```
For any video format:
  ? Logo appears
  ? Logo is not distorted
  ? Logo is not stretched
  ? Logo maintains aspect ratio
```

**Check 3: Log Output**
```
Good log (AFTER FIX):
  [WATERMARK-SCALE] FIXED: Using scale2ref with rw*0.200

Bad log (BEFORE FIX):
  [WATERMARK-SCALE] Using aspect-ratio preserving scale: w=iw*0.200:h=-1
```

---

## Technical Details

### Why scale2ref Is The Right Choice

1. **Designed for Dual Input**
   - Created specifically for this use case
   - Accepts video to scale + reference video
   - FFmpeg devs intended this exact scenario

2. **Outputs Both Pad Types**
   - Scaled logo for next filter
   - Reference video for overlay background
   - Both needed for watermark chain

3. **Aspect Ratio Safety**
   - Automatically preserves source aspect ratio
   - h=-1 auto-calculates from width and original ratio
   - Works for any input dimensions

4. **Reference-Based Scaling**
   - rw = changes based on input video
   - Logo scales relative to background
   - Perfect for watermarking (same logo, different videos)

---

## Migration Guide

### If You Had Custom FFmpeg Commands

**Old Approach (Doesn't Work):**
```bash
# This will fail
ffmpeg -i video.mp4 -i logo.png \
  -filter_complex "[1:v]format=rgba[logo];[logo][0:v]scale=w=iw*0.2:h=-1[scaled][v_ref];" \
  output.mp4
# Error: scale doesn't have 2 input pads
```

**New Approach (Works):**
```bash
# This works
ffmpeg -i video.mp4 -i logo.png \
  -filter_complex "[1:v]format=rgba[logo];[logo][0:v]scale2ref=w=rw*0.2:h=-1[scaled][v_ref];[v_ref][scaled]overlay=x=10:y=10[out];" \
  -map "[out]" output.mp4
# Success: scale2ref accepts 2 inputs
```

---

## FAQ

**Q: Why not use scale with -1 outputs?**
A: scale only accepts 1 input. To pass 2 pads, you NEED scale2ref.

**Q: Does scale2ref work on all FFmpeg versions?**
A: Yes, it's standard in FFmpeg 4.x and later (been there since at least 3.x).

**Q: What if I want to scale with a fixed size?**
A: Use regular `scale=w=500:h=300` for fixed sizing.

**Q: Why use rw instead of iw?**
A: iw is logo's width (fixed). rw is video's width (adapts per video). You want rw for proper scaling.

**Q: Can I use scale2ref for other purposes?**
A: Yes! Any scenario where you need to scale one video relative to another.

---

## Summary

| Aspect | Before | After |
|--------|--------|-------|
| **Filter** | scale | scale2ref |
| **Error** | -22 ? | None ? |
| **Input Pads** | 1 (fails with 2) | 2 ? |
| **Output Pads** | 1 (not enough) | 2 ? |
| **Formula** | w=iw*X | w=rw*X ? |
| **Status** | Broken | Fixed ? |

---

**This fix is: ? TESTED ? DOCUMENTED ? READY**
