using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TitanEngine
{
    public static class TitanTransitionGraphBuilder
    {
        public static (string FilterGraph, string VideoOutputTag, string AudioOutputTag, double TotalDuration) BuildFilterGraph(
            IReadOnlyList<TitanClipMetadata> clips,
            TitanTransitionSettings settings,
            int targetWidth,
            int targetHeight)
        {
            if (clips == null || clips.Count < 2)
                throw new ArgumentException("TitanTransitionGraphBuilder requires at least 2 clips.", nameof(clips));

            var inv = CultureInfo.InvariantCulture;
            int n = clips.Count;

            // Auto-resolve target resolution if not explicitly set
            int tw = targetWidth > 0 ? targetWidth : (clips[0].Width > 0 ? clips[0].Width : 1080);
            int th = targetHeight > 0 ? targetHeight : (clips[0].Height > 0 ? clips[0].Height : 1920);

            tw = Math.Max(2, (tw & 1) == 0 ? tw : tw + 1);
            th = Math.Max(2, (th & 1) == 0 ? th : th + 1);

            // Determine maximum safe transition duration D
            double maxAllowedD = double.MaxValue;
            foreach (var clip in clips)
            {
                double validDur = double.IsNaN(clip.DurationSeconds) || double.IsInfinity(clip.DurationSeconds) ? 1.0 : clip.DurationSeconds;
                double halfDur = Math.Max(0.1, validDur * 0.45);
                if (halfDur < maxAllowedD)
                    maxAllowedD = halfDur;
            }

            double durationD = Math.Clamp(settings.DurationSeconds, 0.1, Math.Min(3.0, maxAllowedD));
            bool isFadeBlur = settings.TransitionType == TitanTransitionType.FadeBlur;

            var sb = new StringBuilder();

            if (isFadeBlur)
            {
                double halfD = durationD / 2.0;
                double step = halfD / 3.0;

                // 1. Normalize & apply sequential two-phase progressive blur ramps (Video 1 blurs out -> Video 2 unblurs in)
                for (int i = 0; i < n; i++)
                {
                    double rawDur = clips[i].DurationSeconds;
                    double clipDur = Math.Max(0.1, double.IsNaN(rawDur) || double.IsInfinity(rawDur) ? 1.0 : rawDur);

                    double tailStart = Math.Max(0.0, clipDur - halfD);
                    double t1_tail = tailStart + step * 1;
                    double t2_tail = tailStart + step * 2;

                    double t1_head = step * 1;
                    double t2_head = step * 2;
                    double t3_head = halfD;

                    string sTailStart = tailStart.ToString("F3", inv);
                    string s1_tail = t1_tail.ToString("F3", inv);
                    string s2_tail = t2_tail.ToString("F3", inv);

                    string d1_head = t1_head.ToString("F3", inv);
                    string d2_head = t2_head.ToString("F3", inv);
                    string d3_head = t3_head.ToString("F3", inv);

                    string blurFilter = "";

                    if (i == 0)
                    {
                        blurFilter = $",avgblur=sizeX=16:sizeY=16:enable='between(t,{sTailStart},{s1_tail})'" +
                                     $",avgblur=sizeX=36:sizeY=36:enable='between(t,{s1_tail},{s2_tail})'" +
                                     $",avgblur=sizeX=64:sizeY=64:enable='gte(t,{s2_tail})'";
                    }
                    else if (i == n - 1)
                    {
                        blurFilter = $",avgblur=sizeX=64:sizeY=64:enable='lte(t,{d1_head})'" +
                                     $",avgblur=sizeX=36:sizeY=36:enable='between(t,{d1_head},{d2_head})'" +
                                     $",avgblur=sizeX=16:sizeY=16:enable='between(t,{d2_head},{d3_head})'";
                    }
                    else
                    {
                        blurFilter = $",avgblur=sizeX=64:sizeY=64:enable='st(0,t);lte(ld(0),{d1_head})+gte(ld(0),{s2_tail})'" +
                                     $",avgblur=sizeX=36:sizeY=36:enable='st(0,t);between(ld(0),{d1_head},{d2_head})+between(ld(0),{s1_tail},{s2_tail})'" +
                                     $",avgblur=sizeX=16:sizeY=16:enable='st(0,t);between(ld(0),{d2_head},{d3_head})+between(ld(0),{sTailStart},{s1_tail})'";
                    }

                    sb.Append($"[{i}:v]fps=30,scale={tw}:{th}:force_original_aspect_ratio=increase,crop={tw}:{th},setsar=1,format=yuv420p{blurFilter}[v{i}_norm];");
                }

                // Concat video streams directly (zero double-exposure dissolve overlay!)
                for (int i = 0; i < n; i++)
                {
                    sb.Append($"[v{i}_norm]");
                }
                sb.Append($"concat=n={n}:v=1:a=0[vout_trans];");
            }
            else
            {
                string xfadeType = TitanTransitionLibrary.ResolveXfadeName(settings.TransitionType);

                // 1. Standard video stream normalization
                for (int i = 0; i < n; i++)
                {
                    sb.Append($"[{i}:v]fps=30,scale={tw}:{th}:force_original_aspect_ratio=increase,crop={tw}:{th},setsar=1,format=yuv420p[v{i}_norm];");
                }

                // 2. Video XFade chaining
                double currentOffset = 0.0;
                string lastVideoTag = "[v0_norm]";

                for (int i = 0; i < n - 1; i++)
                {
                    double rawDur = clips[i].DurationSeconds;
                    double clipDur = Math.Max(durationD + 0.1, double.IsNaN(rawDur) || double.IsInfinity(rawDur) ? 1.0 : rawDur);

                    currentOffset += (clipDur - durationD);
                    if (currentOffset < 0) currentOffset = 0;

                    string nextVideoInput = $"[v{i + 1}_norm]";
                    string outVideoTag = (i == n - 2) ? "[vout_trans]" : $"[vtrans_{i}]";

                    string offsetStr = currentOffset.ToString("F3", inv);
                    string durStr = durationD.ToString("F3", inv);

                    sb.Append($"{lastVideoTag}{nextVideoInput}xfade=transition={xfadeType}:duration={durStr}:offset={offsetStr}{outVideoTag};");
                    lastVideoTag = outVideoTag;
                }
            }

            // Calculate total timeline duration
            double totalDuration = 0.0;
            for (int i = 0; i < n; i++)
            {
                double clipDur = Math.Max(0.1, double.IsNaN(clips[i].DurationSeconds) || double.IsInfinity(clips[i].DurationSeconds) ? 1.0 : clips[i].DurationSeconds);
                totalDuration += clipDur;
            }
            if (!isFadeBlur)
            {
                totalDuration -= (n - 1) * durationD;
            }

            // 3. Audio AcrossFade chaining with full audio stream normalization
            bool anyAudio = false;
            for (int i = 0; i < n; i++)
            {
                if (clips[i].HasAudio)
                {
                    anyAudio = true;
                    break;
                }
            }

            string audioOutTag = "[aout_trans]";

            if (anyAudio)
            {
                // Normalize audio stream for each clip (generate silent audio if clip has no audio)
                for (int i = 0; i < n; i++)
                {
                    if (clips[i].HasAudio)
                    {
                        sb.Append($"[{i}:a]aformat=sample_rates=48000:channel_layouts=stereo,asetpts=PTS-STARTPTS[a{i}_norm];");
                    }
                    else
                    {
                        double clipDur = Math.Max(0.1, clips[i].DurationSeconds);
                        string durStr = clipDur.ToString("F3", inv);
                        sb.Append($"anullsrc=channel_layout=stereo:sample_rate=48000,atrim=duration={durStr},asetpts=PTS-STARTPTS[a{i}_norm];");
                    }
                }

                string lastAudioTag = "[a0_norm]";
                string dStr = durationD.ToString("F3", inv);

                for (int i = 0; i < n - 1; i++)
                {
                    string nextAudioInput = $"[a{i + 1}_norm]";
                    string outAudioTag = (i == n - 2) ? "[aout_trans]" : $"[atrans_{i}]";

                    sb.Append($"{lastAudioTag}{nextAudioInput}acrossfade=d={dStr}:c1=tri:c2=tri{outAudioTag}");
                    if (i < n - 2) sb.Append(";");
                    lastAudioTag = outAudioTag;
                }
            }
            else
            {
                audioOutTag = string.Empty;
                if (sb.Length > 0 && sb[sb.Length - 1] == ';')
                {
                    sb.Remove(sb.Length - 1, 1);
                }
            }

            return (sb.ToString(), "[vout_trans]", audioOutTag, totalDuration);
        }
    }
}
