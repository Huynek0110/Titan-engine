# UPSCALING TUNING GUIDE - Practical Examples

## ?? Real-World Scenarios

### Scenario 1: YouTube 360p Video ? 1080p

**Problem**: Low-res YouTube video needs to be 1080p
**Source**: 360p
**Target**: 1080p (3x upscale)

**Recommended Settings**:
```
Resolution: 1920x1080
Upscale Mode: 2x Upscale (FSR + DLSS)
Sharpness: 1.0 (Balanced)
Hardware: NVIDIA NVENC or AMD AMF
```

**Why**:
- 2x mode sufficient for 3x upscale
- Balanced sharpness avoids artifacting
- GPU acceleration handles the workload

**Expected Result**:
```
? Text readable
? No visible aliasing
? Colors vibrant
? Smooth motion preserved
```

---

### Scenario 2: Mobile TikTok/Instagram ? 4K Display

**Problem**: Short mobile video (480p) needs 4K export
**Source**: 480p (9:16 vertical)
**Target**: 2160x3840 (9x upscale!)

**Recommended Settings**:
```
Resolution: 2160x3840 (Custom - Vertical)
Upscale Mode: 4x Upscale (Ultra Quality)
Sharpness: 1.5 (High)
Hardware: NVIDIA NVENC (p7 best quality)
Bitrate: Boost Quality
```

**Why**:
- 4x mode needed for extreme upscale
- High sharpness recovers detail
- Best quality preset minimizes artifacts

**Expected Result**:
```
? 4K-like appearance
? Minimal visible upscaling
? Detail enhancement noticeable
? Processing time: ~5-10 min
```

---

### Scenario 3: Documentary Film 720p ? 1080p

**Problem**: Documentary needs 1080p for distribution
**Source**: 720p
**Target**: 1080p (1.5x upscale)

**Recommended Settings**:
```
Resolution: 1920x1080
Upscale Mode: 2x Upscale (FSR + DLSS)
Sharpness: 0.7 (Soft)
Hardware: CPU or GPU
Color Grading: Cinematic (optional)
```

**Why**:
- Small upscale ratio (1.5x) = easier
- Soft sharpness prevents over-processing
- Documentary = soft details preferred
- Cinematic color grading adds production value

**Expected Result**:
```
? Minimal upscaling artifacts
? Smooth, professional appearance
? Colors preserved
? Fast processing
```

---

### Scenario 4: Gaming Livestream - 480p ? 1440p Archive

**Problem**: Archive gaming stream in higher resolution
**Source**: 480p (stream capture)
**Target**: 1440p (3x upscale)

**Recommended Settings**:
```
Resolution: 2560x1440
Upscale Mode: 4x Upscale (Ultra Quality)
Sharpness: 1.5 (High)
Hardware: GPU (NVIDIA preferred for gaming)
```

**Why**:
- Gaming content needs sharp text/UI
- 4x mode handles motion artifacts better
- High sharpness = crisp UI elements
- GPU efficient for real-time playback

**Expected Result**:
```
? Game text readable
? HUD elements clear
? Motion smooth
? Gaming experience enhanced
```

---

### Scenario 5: Old VHS Tape Digitization ? 1080p

**Problem**: Restore old VHS (degraded quality) to 1080p
**Source**: 480i (interlaced, noisy)
**Target**: 1080p (2.25x upscale)

**Recommended Settings**:
```
Resolution: 1920x1080
Upscale Mode: 2x Upscale (FSR + DLSS)
Sharpness: 0.8 (Medium-Soft)
Hardware: CPU (slower but no artifacts)
Color Grading: Warm (optional - restore color)
```

**Why**:
- Medium sharpness avoids noise amplification
- DLSS color preservation helps old footage
- CPU safer for delicate restoration
- Warm color grading restores faded tape look

**Expected Result**:
```
? Noise suppressed
? Details recovered where possible
? Colors refreshed
? Vintage feel preserved
```

---

## ??? Sharpness Slider Recommendations

### By Content Type

```
Content Type          Recommended    Reason
?????????????????????????????????????????????????
Text/UI               1.5 - 2.0      Need crisp edges
Screenshots           1.8 - 2.0      Maximum clarity
Photos                1.0 - 1.3      Natural look
Documentaries         0.6 - 1.0      Smooth appearance
Movies/Cinematic      0.8 - 1.2      Professional feel
Gaming                1.3 - 1.8      Sharp details needed
Animated Content      1.0 - 1.5      Depends on art style
Action/Sports         1.2 - 1.6      Fast motion needs clarity
Soft/Dream Content    0.4 - 0.8      Maintain softness
```

---

## ?? Quality vs Performance Matrix

### 2x Upscale Mode

```
Sharpness  Quality   Processing   Artifacts   Recommended For
?????????????????????????????????????????????????????????????
0.0        Good      ?????   Minimal     Fast preview
0.5        Very Good ????    Minimal     Soft content
1.0        Excellent ???     None        Balanced (default)
1.5        Great     ??       Minimal     Sharp content
2.0        Good*     ?         Possible    Ultra-sharp text
*May show artifacts
```

### 4x Upscale Mode

```
Sharpness  Quality      Processing   Artifacts   Recommended For
??????????????????????????????????????????????????????????????
0.0        Very Good    ??       Minimal     Preview quality
0.5        Great        ?          Minimal     Soft content
1.0        Excellent    ?          None        Balanced (default)
1.5        Outstanding  ?          Very Rare   Sharp content
2.0        Outstanding* ?          Very Rare   Maximum quality*
*Longest processing time
```

---

## ?? Processing Time Estimates

**System**: NVIDIA RTX 3060 Ti (example)

### 2x Upscale Mode

```
Video Length   480p?1080p   720p?1440p   1080p?2160p
??????????????????????????????????????????????????
1 minute       45 seconds   60 seconds    90 seconds
5 minutes      3.5 min      5 min         7.5 min
10 minutes     7 min        10 min        15 min
1 hour         42 min       60 min        90 min
```

### 4x Upscale Mode

```
Video Length   480p?1920p   480p?2160p   720p?2880p
??????????????????????????????????????????????????
1 minute       2 min        2.5 min      3 min
5 minutes      10 min       12.5 min     15 min
10 minutes     20 min       25 min       30 min
1 hour         2 hour       2.5 hour     3 hour
```

---

## ? Before/After Examples

### Example 1: YouTube 480p Upscale

**Before** (Raw Lanczos):
```
- Text: Blurry, hard to read
- Edges: Soft, lack definition
- Colors: Dull, washed out
- Artifacts: Minor aliasing on edges
```

**After** (4x Mode, Sharpness 1.5):
```
- Text: Sharp, easily readable ?
- Edges: Crisp definition ?
- Colors: Vibrant, saturated ?
- Artifacts: None visible ?
```

---

### Example 2: Gaming 480p Upscale

**Before** (2x Mode, Sharpness 0.5):
```
- UI Text: Slightly soft
- HUD Elements: Clear but not crisp
- Artifacts: Minimal
- Performance: Fast
```

**After** (2x Mode, Sharpness 1.5):
```
- UI Text: Very sharp ?
- HUD Elements: Crystal clear ?
- Artifacts: Still minimal ?
- Performance: Still acceptable ?
```

---

## ?? Troubleshooting

### Issue: Result too blurry
```
Solution:
1. Increase Sharpness to 1.2-1.5
2. Use 4x Upscale mode instead of 2x
3. Check if hardware encoder causing loss (use CPU for testing)
```

### Issue: Result too sharp/noisy
```
Solution:
1. Decrease Sharpness to 0.5-0.8
2. Add color grading (softens slightly)
3. Use 2x Upscale mode instead of 4x
```

### Issue: Processing takes too long
```
Solution:
1. Use 2x Upscale mode (faster than 4x)
2. Reduce Sharpness value
3. Use GPU acceleration (check Hardware Profile)
4. Reduce target resolution
```

### Issue: Color looks wrong
```
Solution:
1. Sharpness at 1.0 (default) to avoid saturation
2. Add Color Grading to correct tone
3. Use CPU encoder for testing
```

---

## ?? Advanced Tips

### Batch Processing Different Content

```
Job 1 (Text-heavy):    2x Mode, Sharpness 1.8
Job 2 (Documentary):   2x Mode, Sharpness 0.7  
Job 3 (Mobile):        4x Mode, Sharpness 1.5
```

### Layering with Color Grading

```
Upscale: 4x Mode, Sharpness 1.0
+ Color Grading: Cinematic
+ Result: Professional 4K look
```

### A/B Testing

```
Test 1: 2x Mode, Sharpness 1.0 ? 2 min
Test 2: 4x Mode, Sharpness 1.0 ? 10 min
Compare and choose best for final batch
```

---

## ?? Checklist for Best Results

- [ ] Source video codec supported by FFmpeg
- [ ] Target resolution chosen appropriately
- [ ] Upscale mode matches content type
- [ ] Sharpness slider set for content
- [ ] Hardware profile matches GPU availability
- [ ] Bitrate sufficient for quality level
- [ ] Test with 1-minute clip first
- [ ] Compare output with original
- [ ] Adjust sharpness if needed
- [ ] Run final batch when satisfied

---

## ?? Getting Started

### Quick Start Template

1. **Low Quality Source** (480p or less)
   ```
   Upscale: 4x Ultra Quality
   Sharpness: 1.5
   ```

2. **Medium Quality Source** (720p)
   ```
   Upscale: 2x FSR + DLSS
   Sharpness: 1.0
   ```

3. **Already Good Quality** (1080p+)
   ```
   Upscale: Off (Original Size)
   Sharpness: N/A
   ```

---

**Last Updated**: 2024
**Version**: 1.0
