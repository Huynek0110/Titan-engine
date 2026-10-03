Snow overlay assets for Titan Engine.

Optional files:
- snow_soft_01.webm
- snow_bokeh_01.webm
- snow_heavy_01.webm
- snow_windy_01.webm
- snow_cinematic_01.mp4

If no asset is present, Titan Engine will auto-generate a transparent snow overlay
sequence at render time and loop it with FFmpeg.

Bundled default:
- snow_cinematic_01.mp4
  Source: Pixabay video 247477
  https://pixabay.com/videos/snow-snowfall-overlay-vfx-247477/
  Type: black background snowfall overlay (blend=screen)

Preferred asset specs:
- 1080x1920 or larger
- 30 fps
- 4 to 8 seconds
- no audio
- alpha WebM/MOV preferred
- black background MP4 also supported via blend=screen
