using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using TitanEngine.Core;

namespace TitanEngine.Avalonia.ViewModels;

/// <summary>
/// Khu SOUNDBAR — mirror toàn bộ máy đồng bộ UI của MainWindow (PopulateSoundBarComboBoxes,
/// ApplySoundBarSizePercent, SyncSoundBarSizeFromBoxes, settle-commit 700ms,
/// ComputeSoundBarPresetAnchor, canvas aspect, tooltip vị trí).
/// QUAN TRỌNG (Giai đoạn 5): feature soundbar = WPF-stay (bộ dựng hình RenderTargetBitmap) →
/// checkbox Bật SOUNDBAR bị DISABLE + tooltip "chưa hỗ trợ trên bản này"; preview chỉ hiện hint.
/// Toàn bộ logic đồng bộ vẫn được port đầy đủ để Giai đoạn 4 nối lại chỉ cần bật UI.
/// </summary>
public partial class SoundbarViewModel : ObservableObject
{
    public ObservableCollection<ComboOption> StyleOptions { get; } = new();
    public ObservableCollection<ComboOption> PaletteOptions { get; } = new();
    public ObservableCollection<ComboOption> AudioSourceOptions { get; } = new(
        new[]
        {
            new ComboOption("Tự động (ưu tiên audio ngoài)", "auto"),
            new ComboOption("Chỉ audio ngoài", "external"),
            new ComboOption("Audio 1 - nghe riêng track 1", "audio1"),
            new ComboOption("Audio 2 - nghe riêng track 2 (vocal)", "audio2"),
            new ComboOption("Chỉ audio trong video", "video"),
        });
    public ObservableCollection<ComboOption> SizePresetOptions { get; } = new();
    public ObservableCollection<ComboOption> PositionOptions { get; } = new(
        new[]
        {
            new ComboOption("Dưới - Giữa", "bottom-center"),
            new ComboOption("Dưới - Trái", "bottom-left"),
            new ComboOption("Dưới - Phải", "bottom-right"),
            new ComboOption("Giữa khung", "center"),
            new ComboOption("Trên - Giữa", "top-center"),
            new ComboOption("Trên - Trái", "top-left"),
            new ComboOption("Trên - Phải", "top-right"),
            new ComboOption("Tuỳ chỉnh (X/Y %)", UiLogic.SoundBarCustomPositionTag),
        });

    [ObservableProperty] private bool _enableSoundBar;
    [ObservableProperty] private ComboOption _selectedStyle = new("Neon Bars (đáy)", "neon-bars");
    [ObservableProperty] private ComboOption _selectedPalette = new("NCS Cyan", "ncs-cyan");
    [ObservableProperty] private ComboOption _selectedAudioSource = new("Tự động (ưu tiên audio ngoài)", "auto");
    [ObservableProperty] private ComboOption _selectedSizePreset = new("Vừa (100%)", "100");
    [ObservableProperty] private ComboOption _selectedPosition = new("Tuỳ chỉnh (X/Y %)", UiLogic.SoundBarCustomPositionTag);
    [ObservableProperty] private double _opacity = 1.0;
    [ObservableProperty] private double _sensitivity = 1.0;
    [ObservableProperty] private double _smoothing = 1.0;
    [ObservableProperty] private double _barCount = 64;
    [ObservableProperty] private double _size = 100;
    [ObservableProperty] private string _xPercent = "50";
    [ObservableProperty] private string _yPercent = "82";
    [ObservableProperty] private string _widthPercent = "62";
    [ObservableProperty] private string _heightPercent = "26";
    [ObservableProperty] private string _sizeValueText = "100%";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _positionTooltip =
        "Chọn vị trí dựng sẵn. Vị trí được ghi vào X/Y (%) bên dưới - sửa tay ô X/Y sẽ tự chuyển sang 'Tuỳ chỉnh'. " +
        "CHƯA xác định được khung xuất (Original mà chưa có file để đo), nên đang tạm dùng 16:9 - " +
        "các vị trí dựng sẵn có thể lệch, và sẽ tự tính lại khi bạn chọn file hoặc đổi độ phân giải.";

    // ── canvas aspect state (mirror MainWindow) ──
    private double _canvasAspect = UiLogic.SoundBarCanvasAspectFallback;
    private bool _canvasAspectIsKnown;
    private int _sourceWidth;
    private int _sourceHeight;
    private bool _probeInFlight;
    private int _probeGeneration;
    private bool _probeRerunRequested;

    // ── guards + settle timer (mirror MainWindow) ──
    private bool _applyingSize;
    private bool _applyingPosition;
    private readonly DispatcherTimer _sizeEditCommitTimer;

    public event Action<string>? LogRequested;

    public SoundbarViewModel()
    {
        // ── PopulateSoundBarComboBoxes: styles từ bảng chuẩn trong Core ──
        try
        {
            int added = 0;
            foreach (SoundBarStyleInfo style in SoundBarVisualizerPreset.AllStyles)
            {
                if (string.IsNullOrWhiteSpace(style.StyleId)) continue;
                string label = UiLogic.SoundBarStyleDisplayLabels.TryGetValue(style.StyleId, out string? custom)
                    ? custom
                    : style.Name;
                StyleOptions.Add(new ComboOption(label, style.StyleId));
                added++;
            }
            if (added == 0) throw new InvalidOperationException("SoundBarVisualizerPreset.AllStyles returned no usable styles.");
        }
        catch (Exception ex)
        {
            LogRequested?.Invoke($"[SOUNDBAR-WARN] Style list unavailable ({ex.Message}); falling back to neon-bars.");
            StyleOptions.Clear();
            StyleOptions.Add(new ComboOption("Neon Bars (đáy)", "neon-bars"));
        }
        SelectedStyle = StyleOptions[0];

        try
        {
            int added = 0;
            foreach (ColorPreset preset in ColorPresets.All)
            {
                if (string.IsNullOrWhiteSpace(preset.Id)) continue;
                PaletteOptions.Add(new ComboOption(preset.Name, preset.Id));
                added++;
            }
            if (added == 0) throw new InvalidOperationException("ColorPresets.All returned no usable palettes.");
        }
        catch (Exception ex)
        {
            LogRequested?.Invoke($"[SOUNDBAR-WARN] Palette list unavailable ({ex.Message}); falling back to ncs-cyan.");
            PaletteOptions.Clear();
            PaletteOptions.Add(new ComboOption("NCS Cyan", "ncs-cyan"));
        }
        SelectedPalette = PaletteOptions[0];

        foreach ((string label, string tag) in UiLogic.BuildSoundBarSizePresetEntries())
            SizePresetOptions.Add(new ComboOption(label, tag));
        SizePresetOptions.Add(new ComboOption("Tuỳ chỉnh", UiLogic.SoundBarSizeCustomTag));
        int idx100 = FindSizePresetIndex(100.0);
        if (idx100 >= 0) SelectedSizePreset = SizePresetOptions[idx100];

        // Position combo trỏ "Tuỳ chỉnh" để khớp X=50/Y=82 (comment của MainWindow:2050).
        int customIdx = -1;
        for (int i = 0; i < PositionOptions.Count; i++)
            if (PositionOptions[i].Tag == UiLogic.SoundBarCustomPositionTag) { customIdx = i; break; }
        if (customIdx >= 0) SelectedPosition = PositionOptions[customIdx];

        _sizeEditCommitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _sizeEditCommitTimer.Tick += (_, _) =>
        {
            _sizeEditCommitTimer.Stop();
            CommitSizeBoxEdit();
        };

        UpdateDescription();
    }

    public string SelectedStyleId => SelectedStyle.Tag ?? "neon-bars";

    private void Log(string message) => LogRequested?.Invoke(message);

    partial void OnEnableSoundBarChanged(bool value)
    {
        // ── ChkEnableSoundBar_Changed ──
        CancelSizeEditCommit();
    }

    partial void OnSelectedStyleChanged(ComboOption value) { UpdateDescription(); }
    partial void OnSelectedPaletteChanged(ComboOption value) { }
    partial void OnSelectedAudioSourceChanged(ComboOption value) { }

    // ── MainWindow: CmbSoundBarSizePreset_Changed ──
    partial void OnSelectedSizePresetChanged(ComboOption value)
    {
        if (_applyingSize) return;
        if (value?.Tag is string tag && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double percent))
        {
            ApplySizePercent(percent);
            return;
        }
        SyncSizePresetCombo(Size);
    }

    // ── MainWindow: CmbSoundBarPosition_Changed ──
    partial void OnSelectedPositionChanged(ComboOption value) => ApplyPositionPreset();

    // ── MainWindow: SldSoundBarSize_ValueChanged ──
    partial void OnSizeChanged(double value)
    {
        if (_applyingSize) return;
        ApplySizePercent(value);
    }

    // ── MainWindow: SldSoundBar_ValueChanged / TxtSoundBarPercent_TextChanged ──
    partial void OnOpacityChanged(double value) { }
    partial void OnSensitivityChanged(double value) { }
    partial void OnSmoothingChanged(double value) { }
    partial void OnBarCountChanged(double value) { }

    partial void OnXPercentChanged(string value) => OnPercentBoxEdited(isXY: true);
    partial void OnYPercentChanged(string value) => OnPercentBoxEdited(isXY: true);
    partial void OnWidthPercentChanged(string value) => OnPercentBoxEdited(isXY: false);
    partial void OnHeightPercentChanged(string value) => OnPercentBoxEdited(isXY: false);

    private void OnPercentBoxEdited(bool isXY)
    {
        if (isXY)
        {
            if (!_applyingPosition)
                SelectCustomPosition();
        }
        else
        {
            if (!_applyingSize)
                SyncSizeFromBoxes();
        }
    }

    /// <summary>Gọi khi ô size mất focus — mirror TxtSoundBarSizeBox_LostKeyboardFocus.</summary>
    public void OnSizeBoxLostFocus() => ArmSizeEditCommit();

    // ── MainWindow: ApplySoundBarSizePercent ──
    public void ApplySizePercent(double percent)
    {
        double clamped = UiLogic.ClampSoundBarSizePercent(percent);
        if (Math.Abs(clamped - percent) > 0.005)
        {
            Log($"[SOUNDBAR] Size {percent:0.##}% clamped to {clamped:0.##}% - past that scale the "
                + $"{UiLogic.SoundBarBaseWidthPercent:0}x{UiLogic.SoundBarBaseHeightPercent:0} footprint is wider than the frame "
                + $"can inset by {UiLogic.SoundBarAnchorMarginFraction * 100:0.##}% on both sides, so the "
                + $"corner presets would collapse to the middle.");
        }

        UiLogic.ComputeSoundBarSizeFootprint(clamped, out double height, out double width);
        CancelSizeEditCommit();

        _applyingSize = true;
        try
        {
            HeightPercent = height.ToString("0.##", CultureInfo.InvariantCulture);
            WidthPercent = width.ToString("0.##", CultureInfo.InvariantCulture);
            Size = clamped;
            SizeValueText = $"{Math.Round(clamped, 0):0}%";
        }
        finally
        {
            _applyingSize = false;
        }

        SyncSizePresetCombo(clamped);
        ApplyPositionPreset();
    }

    // ── MainWindow: SyncSoundBarSizeFromBoxes ──
    private void SyncSizeFromBoxes()
    {
        if (_applyingSize) return;

        double height = UiLogic.ClampSoundBarTypedHeightPercent(UiLogic.ParsePercent(HeightPercent, UiLogic.SoundBarBaseHeightPercent));
        double rawPercent = (height / UiLogic.SoundBarBaseHeightPercent) * 100.0;
        double percent = UiLogic.ClampSoundBarSizePercent(rawPercent);

        _applyingSize = true;
        try
        {
            Size = percent;
            SizeValueText = $"{Math.Round(percent, 0):0}%";
        }
        finally
        {
            _applyingSize = false;
        }

        SyncSizePresetCombo(percent);
        ApplyPositionPreset();

        double typedWidth = UiLogic.ParsePercent(WidthPercent, UiLogic.SoundBarBaseWidthPercent);
        bool widthUnrepresentable = Math.Abs(typedWidth - UiLogic.ClampSoundBarTypedWidthPercent(typedWidth)) > 0.005;
        if (widthUnrepresentable || Math.Abs(rawPercent - percent) > 0.005)
            ArmSizeEditCommit();
    }

    private void CancelSizeEditCommit() => _sizeEditCommitTimer.Stop();

    private void ArmSizeEditCommit() => _sizeEditCommitTimer.Start();

    // ── MainWindow: CommitSoundBarSizeBoxEdit ──
    private void CommitSizeBoxEdit()
    {
        try
        {
            if (_applyingSize) return;

            double typedHeight = UiLogic.ParsePercent(HeightPercent, UiLogic.SoundBarBaseHeightPercent);
            double height = UiLogic.ClampSoundBarTypedHeightPercent(typedHeight);
            double rawPercent = (height / UiLogic.SoundBarBaseHeightPercent) * 100.0;
            double percent = UiLogic.ClampSoundBarSizePercent(rawPercent);

            if (Math.Abs(rawPercent - percent) > 0.005)
            {
                Log($"[SOUNDBAR] Height box {typedHeight:0.##}% needs {rawPercent:0.##}%, which is outside the "
                    + $"{UiLogic.SoundBarSizeMin:0}%-{UiLogic.SoundBarSizeMax:0.##}% the size control covers at the "
                    + $"{UiLogic.SoundBarBaseWidthPercent:0}x{UiLogic.SoundBarBaseHeightPercent:0} footprint. The boxes now read "
                    + $"{UiLogic.SoundBarBaseHeightPercent * (percent / 100.0):0.##}% x {UiLogic.SoundBarBaseHeightPercent * (percent / 100.0) * UiLogic.SoundBarBaseAspect:0.##}% - "
                    + $"keeping {typedHeight:0.##}% would have rendered a figure that breaks that ratio. "
                    + $"The previous commit suppressed this line because it also fires mid-word; it fires here only once the edit has settled.");
                ApplySizePercent(percent);
                return;
            }

            double typedWidth = UiLogic.ParsePercent(WidthPercent, UiLogic.SoundBarBaseWidthPercent);
            double clampedWidth = UiLogic.ClampSoundBarTypedWidthPercent(typedWidth);
            if (Math.Abs(typedWidth - clampedWidth) <= 0.005)
                return;

            Log($"[SOUNDBAR] Width box {typedWidth:0.##}% is over the {UiLogic.SoundBarWidthPercentCap:0.##}% ceiling "
                + $"the corner presets need in order to leave {UiLogic.SoundBarAnchorMarginFraction * 100:0.##}% of frame on both sides; "
                + $"past it a left and a right anchor collapse to the same centre. Clamped to {clampedWidth:0.##}%.");

            _applyingSize = true;
            try { WidthPercent = clampedWidth.ToString("0.##", CultureInfo.InvariantCulture); }
            finally { _applyingSize = false; }
        }
        catch (Exception ex)
        {
            Log($"[SOUNDBAR-WARN] Size edit commit failed: {ex.Message}");
        }
    }

    private void SyncSizePresetCombo(double percent)
    {
        if (_applyingSize || SizePresetOptions.Count == 0) return;
        int target = FindSizePresetIndex(percent);
        if (target < 0 || ReferenceEquals(SizePresetOptions[target], SelectedSizePreset)) return;

        _applyingSize = true;
        try { SelectedSizePreset = SizePresetOptions[target]; }
        finally { _applyingSize = false; }
    }

    private int FindSizePresetIndex(double percent)
    {
        int customIndex = -1;
        for (int i = 0; i < SizePresetOptions.Count; i++)
        {
            string tag = SizePresetOptions[i].Tag ?? "";
            if (string.Equals(tag, UiLogic.SoundBarSizeCustomTag, StringComparison.OrdinalIgnoreCase))
            {
                customIndex = i;
                continue;
            }
            if (double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double tagPercent) &&
                Math.Abs(tagPercent - percent) < UiLogic.SoundBarSizePresetMatchTolerance)
                return i;
        }
        return customIndex;
    }

    // ── MainWindow: ApplySoundBarPositionPreset ──
    private void ApplyPositionPreset()
    {
        if (_applyingPosition || SelectedPosition is null) return;

        string tag = SelectedPosition.Tag ?? UiLogic.SoundBarCustomPositionTag;
        if (string.Equals(tag, UiLogic.SoundBarCustomPositionTag, StringComparison.OrdinalIgnoreCase))
            return;

        double widthPercent = UiLogic.ClampSoundBarTypedWidthPercent(UiLogic.ParsePercent(WidthPercent, UiLogic.SoundBarBaseWidthPercent)) / 100.0;
        double heightPercent = UiLogic.ClampSoundBarTypedHeightPercent(UiLogic.ParsePercent(HeightPercent, UiLogic.SoundBarBaseHeightPercent)) / 100.0;

        SoundBarStyleId styleId = SoundBarVisualizerPreset.FindStyle(SelectedStyleId)?.Id ?? SoundBarStyleId.NeonBars;

        (double x, double y) = UiLogic.ComputeSoundBarPresetAnchor(
            tag, widthPercent, heightPercent, _canvasAspect, UiLogic.SoundBarUsesSquarePlot(styleId));

        _applyingPosition = true;
        try
        {
            XPercent = (x * 100.0).ToString("0.##", CultureInfo.InvariantCulture);
            YPercent = (y * 100.0).ToString("0.##", CultureInfo.InvariantCulture);
        }
        finally
        {
            _applyingPosition = false;
        }
    }

    // ── MainWindow: SelectSoundBarCustomPosition ──
    private void SelectCustomPosition()
    {
        if (_applyingPosition) return;
        int customIndex = -1;
        for (int i = 0; i < PositionOptions.Count; i++)
            if (PositionOptions[i].Tag == UiLogic.SoundBarCustomPositionTag) { customIndex = i; break; }
        if (customIndex < 0)
        {
            Log("[SOUNDBAR-WARN] Position combo has no 'custom' entry; X/Y edits cannot be labelled.");
            return;
        }
        if (ReferenceEquals(PositionOptions[customIndex], SelectedPosition)) return;

        _applyingPosition = true;
        try { SelectedPosition = PositionOptions[customIndex]; }
        finally { _applyingPosition = false; }
    }

    // ── MainWindow: UpdateSoundBarDescription ──
    public void UpdateDescription()
    {
        try
        {
            var style = SoundBarVisualizerPreset.FindStyle(SelectedStyleId);
            Description = style?.Description ?? string.Empty;
        }
        catch (Exception ex)
        {
            Log($"[SOUNDBAR-WARN] Style description unavailable: {ex.Message}");
            Description = string.Empty;
        }
    }

    // ── MainWindow: TrySetSoundBarCanvasAspect ──
    private bool TrySetCanvasAspect(int width, int height)
    {
        if (width <= 0 || height <= 0) return false;
        double candidate = width / (double)height;
        if (double.IsNaN(candidate) || double.IsInfinity(candidate)) return false;
        _canvasAspect = Math.Clamp(candidate, UiLogic.SoundBarCanvasAspectMin, UiLogic.SoundBarCanvasAspectMax);
        return true;
    }

    // ── MainWindow: TryGetSoundBarOutputSize (đọc từ ExportViewModel qua callback) ──
    public Func<(int W, int H, bool Ok)>? ResolveOutputSize { get; set; }

    // ── MainWindow: RefreshSoundBarCanvasAspect ──
    public void RefreshCanvasAspect()
    {
        bool resolved = false;
        int w = 0, h = 0;
        if (ResolveOutputSize != null)
        {
            (w, h, resolved) = ResolveOutputSize();
        }

        if (!resolved)
        {
            _canvasAspectIsKnown = false;
            _canvasAspect = UiLogic.SoundBarCanvasAspectFallback;
            UpdatePositionTooltip();
            return;
        }

        double previous = _canvasAspect;
        bool wasKnown = _canvasAspectIsKnown;
        if (!TrySetCanvasAspect(w, h)) return;

        _canvasAspectIsKnown = true;
        if (Math.Abs(_canvasAspect - previous) < 0.0005 && wasKnown) return;
        ApplyPositionPreset();
        UpdatePositionTooltip();
    }

    private void UpdatePositionTooltip()
    {
        if (_canvasAspectIsKnown)
        {
            PositionTooltip =
                "Chọn vị trí dựng sẵn. Vị trí được ghi vào X/Y (%) bên dưới - sửa tay ô X/Y sẽ tự chuyển sang 'Tuỳ chỉnh'. "
                + $"Các vị trí dựng sẵn đang tính cho khung xuất tỉ lệ {_canvasAspect:0.###} (ngang/dọc).";
        }
        else
        {
            PositionTooltip =
                "Chọn vị trí dựng sẵn. Vị trí được ghi vào X/Y (%) bên dưới - sửa tay ô X/Y sẽ tự chuyển sang 'Tuỳ chỉnh'. "
                + "CHƯA xác định được khung xuất (Original mà chưa có file để đo), nên đang tạm dùng 16:9 - "
                + "các vị trí dựng sẵn có thể lệch, và sẽ tự tính lại khi bạn chọn file hoặc đổi độ phân giải.";
        }
    }

    // ── MainWindow: ProbeSoundBarSourceResolutionAsync (phiên bản không generation-race nặng:
    //    giữ latch + generation như bản gốc) ──
    public async System.Threading.Tasks.Task ProbeSourceResolutionAsync(Func<(int W, int H, bool Ok)>? fallbackOutputSize = null)
    {
        int generation = ++_probeGeneration;
        if (_probeInFlight)
        {
            _probeRerunRequested = true;
            return;
        }

        _probeInFlight = true;
        try
        {
            // Nguồn hiện tại do root VM truyền vào qua LastProbedSource.
            string? source = LastProbedSource;
            bool ok = false;
            if (!string.IsNullOrWhiteSpace(source) && System.IO.File.Exists(source))
            {
                var size = await EngineCore.GetVideoResolutionAsync(source);
                if (generation == _probeGeneration)
                {
                    bool ffprobeAvailable = EngineCore.IsFfprobeAvailable;
                    bool isImage = UiLogic.IsImageSourcePath(source);
                    if ((!isImage && !ffprobeAvailable) || size.Width <= 0 || size.Height <= 0 ||
                        (!isImage && size.Width == 1920 && size.Height == 1080))
                    {
                        if (!isImage && !ffprobeAvailable)
                            Log("[SOUNDBAR-WARN] ffprobe not found; cannot resolve 'Original' output resolution. "
                                + "Soundbar position presets are using the 16:9 default until a resolution is chosen explicitly.");
                        else if (!isImage && size.Width == 1920 && size.Height == 1080)
                            Log($"[SOUNDBAR] Probe of '{System.IO.Path.GetFileName(source)}' returned 1920x1080, which is also what a "
                                + "failed probe returns, so it cannot be told apart from a real 16:9 source. Treating 'Original' "
                                + "as unresolved; the 16:9 stand-in gives identical position presets either way.");
                        _sourceWidth = 0;
                        _sourceHeight = 0;
                    }
                    else
                    {
                        _sourceWidth = size.Width;
                        _sourceHeight = size.Height;
                        Log($"[SOUNDBAR] Source probed at {size.Width}x{size.Height} for 'Original' resolution.");
                        ok = true;
                    }
                }
            }
            else
            {
                _sourceWidth = 0;
                _sourceHeight = 0;
            }

            if (!ok && generation == _probeGeneration)
            {
                // Không đo được nguồn: RefreshCanvasAspect sẽ tự quyết theo resolution controls.
            }
        }
        catch (Exception ex)
        {
            if (generation == _probeGeneration)
            {
                _sourceWidth = 0;
                _sourceHeight = 0;
                Log($"[SOUNDBAR-WARN] Source probe failed ({ex.Message}); 'Original' resolution unresolved.");
            }
        }
        finally
        {
            _probeInFlight = false;
        }

        if (_probeRerunRequested)
        {
            _probeRerunRequested = false;
            await ProbeSourceResolutionAsync();
            return;
        }

        RefreshCanvasAspect();
    }

    /// <summary>Root VM gắn đường nguồn hiện tại trước khi gọi ProbeSourceResolutionAsync.</summary>
    public string? LastProbedSource { get; set; }

    /// <summary>Soundbar read source dims cho TryGetSoundBarOutputSize ở root VM.</summary>
    public (int W, int H) ProbedSourceSize => (_sourceWidth, _sourceHeight);

    // ── MainWindow: ApplySoundBarSettingsToJob ──
    public void ApplyToJob(RenderJob job)
    {
        if (job == null) return;

        // Settle-commit đang chờ sẽ sửa box SAU khi job dựng từ chúng — hủy như bản gốc (queue time không phải settle point).
        CancelSizeEditCommit();

        // CaptureSoundBarCanvasAspect: queue time là điểm cuối có TargetWidth/Height.
        if (job.TargetWidth > 0 && job.TargetHeight > 0)
        {
            double previous = _canvasAspect;
            bool wasKnown = _canvasAspectIsKnown;
            if (TrySetCanvasAspect(job.TargetWidth, job.TargetHeight))
            {
                _canvasAspectIsKnown = true;
                if (Math.Abs(_canvasAspect - previous) >= 0.0005 || !wasKnown)
                    ApplyPositionPreset();
            }
        }

        job.EnableSoundBar = EnableSoundBar;
        job.SoundBarStyle = SelectedStyle?.Tag ?? "neon-bars";
        job.SoundBarPalette = SelectedPalette?.Tag ?? "ncs-cyan";
        job.SoundBarAudioSource = SelectedAudioSource?.Tag ?? "auto";
        job.SoundBarXPercent = Math.Clamp(UiLogic.ParsePercent(XPercent, 50.0) / 100.0, 0.0, 1.0);
        job.SoundBarYPercent = Math.Clamp(UiLogic.ParsePercent(YPercent, 82.0) / 100.0, 0.0, 1.0);
        job.SoundBarWidthPercent = UiLogic.ClampSoundBarTypedWidthPercent(UiLogic.ParsePercent(WidthPercent, 62.0)) / 100.0;
        job.SoundBarHeightPercent = UiLogic.ClampSoundBarTypedHeightPercent(UiLogic.ParsePercent(HeightPercent, 26.0)) / 100.0;
        job.SoundBarOpacity = Math.Clamp(Opacity, EngineCore.SoundBarMinOpacity, EngineCore.SoundBarMaxOpacity);
        job.SoundBarSensitivity = Math.Clamp(Sensitivity, EngineCore.SoundBarMinSensitivity, EngineCore.SoundBarMaxSensitivity);
        job.SoundBarSmoothing = Math.Clamp(Smoothing, EngineCore.SoundBarMinSmoothing, EngineCore.SoundBarMaxSmoothing);
        job.SoundBarBarCount = (int)Math.Round(Math.Clamp(BarCount, EngineCore.SoundBarMinBarCount, EngineCore.SoundBarMaxBarCount));
        job.SoundBarRoundness = 1.0;
        job.SoundBarGlowIntensity = 1.0;
        job.SoundBarRingCount = 2;
        job.SoundBarLoopSeconds = 5.0;
    }
}
