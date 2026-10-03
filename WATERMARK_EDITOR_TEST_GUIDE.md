# TITAN ENGINE V89 - WATERMARK EDITOR QUICK TEST GUIDE

## ?? Quick Test Scenarios

### ? Test 1: Vertical Video (TikTok/Shorts)
**Purpose:** Verify Dynamic Aspect Ratio works for 9:16 videos

**Steps:**
1. Click **"ADD VIDEOS"** ? Select a vertical video (1080x1920 or 9:16)
2. Click **"EDIT POSITION (VISUAL)"**
3. **Expected Results:**
   - ? Canvas should be **tall and narrow** (not 16:9)
   - ? Logo should appear **crisp** without distortion
   - ? Log shows: `Canvas resized to XXXxYYY (Video: 1080x1920)`

---

### ? Test 2: Multiple Videos with Ghosting
**Purpose:** Verify Multi-Video Ghosting Preview

**Steps:**
1. Add **3+ videos** (mix of different formats: 16:9, 9:16, 1:1)
2. Click **"EDIT POSITION (VISUAL)"**
3. **Expected Results:**
   - ? Multiple video previews appear **layered** behind logo
   - ? Each layer has **different opacity** (muted colors)
   - ? Logo stays **on top** (fully opaque, draggable)
   - ? Logs show: 
     ```
     [WATERMARK-EDITOR] Extracting preview 1/3...
     [WATERMARK-EDITOR] Ghost layer 1 added (opacity: 0.30)
     [WATERMARK-EDITOR] Ghost layer 2 added (opacity: 0.22)
     ```

---

### ? Test 3: Logo Not Distorted (Aspect Ratio Preservation)
**Purpose:** Verify FFmpeg Scale doesn't distort logo on vertical video

**Steps:**
1. Select **vertical video** (e.g., 1080x1920)
2. Select **rectangular logo** (e.g., 200x100 or 300x100)
3. Set **WatermarkScale** to 0.2-0.3
4. Click **"START BATCH"** to render
5. **Expected Results:**
   - ? Logo on output video is **NOT squeezed/compressed**
   - ? Logo **maintains original aspect ratio** (width:height)
   - ? Log shows: `Using aspect-ratio preserving scale: w=iw*0.200:h=-1`

---

### ? Test 4: Batch Processing with Different Video Sizes
**Purpose:** Verify batch processing works with mix of video dimensions

**Setup:**
```
Queue 3 jobs:
  Job 1: 1920x1080 (16:9 horizontal)
  Job 2: 1080x1920 (9:16 vertical)
  Job 3: 1080x1080 (1:1 square)
```

**Steps:**
1. Set watermark position in editor (using ghosting preview)
2. Click **"START BATCH"**
3. **Expected Results:**
   - ? All 3 videos render successfully
   - ? Logo appears in same position on all 3
   - ? Logo is **NOT distorted** on any video
   - ? No filter errors in logs

---

## ?? Debugging Tips

### ? Issue: Canvas is still 16:9 (not dynamic)
**Solution:**
- Check if video resolution detection works:
  ```log
  [WATERMARK-EDITOR] Canvas resized to XXXxYYY (Video: WIDTHxHEIGHT)
  ```
- If missing, verify `GetVideoResolutionAsync()` is working
- Check FFprobe is found in app directory

### ? Issue: Ghost layers not appearing
**Solution:**
- Check logs for error messages:
  ```log
  [WATERMARK-EDITOR] Failed to load ghost frame X: ...
  ```
- Ensure FFmpeg is working (preview extraction)
- Try with fewer videos (preview extraction takes time)

### ? Issue: Logo appears distorted on output
**Solution:**
- Check FFmpeg command in logs for scale parameter:
  ```
  scale=w=iw*0.200:h=-1  ? Should see -1 for height
  NOT: scale=500:300     ? Bad (fixed size)
  ```
- If wrong, force rebuild and restart app

---

## ?? Expected Log Output (Vertical Video Example)

```log
[WATERMARK-EDITOR] Detecting video resolution...
[WATERMARK-EDITOR] Canvas resized to 337.50x600.00 (Video: 1080x1920)
[WATERMARK-EDITOR] Extracting preview 1/3...
[WATERMARK-EDITOR] Extracting preview 2/3...
[WATERMARK-EDITOR] Extracting preview 3/3...
[WATERMARK-EDITOR] Ghost layer 1 added (opacity: 0.30)
[WATERMARK-EDITOR] Ghost layer 2 added (opacity: 0.22)
[WATERMARK-EDITOR] Ghost layer 3 added (opacity: 0.14)
[WATERMARK-EDITOR] Multi-video ghosting effect applied (3 layers)
[WATERMARK] Saved: Scale=0.200 (relative), Rotation=0.0°, Opacity=1.00, Position=(5.0%, 5.0%)

[START] Job #1: video_1080x1920.mp4
[CONFIG] Watermark: logo.png
[WATERMARK-SCALE] Using aspect-ratio preserving scale: w=iw*0.200:h=-1
[FFMPEG-CMD] ffmpeg -y -i "video_1080x1920.mp4" -i "logo.png" -filter_complex ... -c:a aac -b:a 256k "output.mp4"
[SUCCESS] Job #1 completed
```

---

## ? Features Summary

| Feature | Status | Notes |
|---------|--------|-------|
| Dynamic Aspect Ratio | ? NEW | Auto-fit canvas to video |
| Multi-Video Ghosting | ? NEW | Up to 3 video layers |
| Aspect Ratio Preservation | ? NEW | FFmpeg scale=w=iw*X:h=-1 |
| Batch Processing | ? WORKS | Multiple videos same position |
| Position Editing | ? WORKS | Drag & drop on canvas |
| Scale/Rotation/Opacity | ? WORKS | Sliders in editor |

---

## ?? Success Criteria

? **All tests pass if:**
1. Canvas size changes based on video aspect ratio
2. Multiple preview layers appear with different opacity
3. Logo never appears distorted (even on vertical videos)
4. Batch processing handles mixed video sizes
5. No FFmpeg errors in logs

---

**Test Date:** ___________  
**Tester Name:** ___________  
**Result:** ? PASS / ?? PARTIAL / ? FAIL

