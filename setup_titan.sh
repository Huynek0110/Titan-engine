#!/usr/bin/env bash
# =============================================================================
# TITAN ENGINE AUTO-SETUP — Arch / CachyOS (Linux native)
# Tương đương setup_titan.ps1 (Windows: tải FFmpeg zip → chép cạnh exe).
# Trên Arch/CachyOS: FFmpeg/ffprobe/ffplay là package hệ thống (PATH),
# tool locator của engine tìm cạnh app trước rồi tới PATH → pacman là chuẩn.
# Chạy bằng:  bash setup_titan.sh   (dù shell mặc định là fish)
# =============================================================================
set -u

say()  { printf '\033[36m>>> %s\033[0m\n' "$*"; }
warn() { printf '\033[33m>>> %s\033[0m\n' "$*"; }
err()  { printf '\033[31m>>> %s\033[0m\n' "$*"; }

say "DANG KHOI DONG TITAN SETUP (Arch/CachyOS)..."

# 1. Kiểm tra pacman
if ! command -v pacman >/dev/null 2>&1; then
    err "Khong thay pacman — script danh cho Arch/CachyOS. Cai thu cong: ffmpeg, fonts."
    exit 1
fi

# 2. FFmpeg + ffprobe + ffplay (1 package)
if command -v ffmpeg >/dev/null 2>&1 && command -v ffprobe >/dev/null 2>&1; then
    say "ffmpeg da co: $(command -v ffmpeg) — $(ffmpeg -version 2>/dev/null | head -1)"
else
    say "DANG CAI FFMPEG (sudo pacman -S --needed ffmpeg)..."
    sudo pacman -S --needed ffmpeg || { err "Cai ffmpeg that bai"; exit 1; }
fi

# 3. Font hệ thống cho drawtext/captions tiếng Việt (drawtext fontfile cần .ttf)
if [ -f /usr/share/fonts/TTF/DejaVuSans.ttf ] || [ -f /usr/share/fonts/truetype/dejavu/DejaVuSans.ttf ]; then
    say "Font DejaVu da co (drawtext OK)."
else
    say "DANG CAI FONT (sudo pacman -S --needed noto-fonts ttf-dejavu)..."
    sudo pacman -S --needed noto-fonts ttf-dejavu || warn "Cai font that bai — drawtext se bao thieu font khi render text"
fi

# 4. Vulkan + libplacebo (internal upscale qua ffmpeg libplacebo cần Vulkan device)
if ffmpeg -hide_banner -filters 2>/dev/null | grep -q libplacebo; then
    say "ffmpeg co filter libplacebo (build Arch chuan)."
    if ffmpeg -hide_banner -loglevel error -init_hw_device vulkan=vk -f lavfi \
        -i color=c=black:s=320x240:r=30 -t 1 -vf "libplacebo=w=320:h=240" -f null - >/dev/null 2>&1; then
        say "Vulkan device init + libplacebo test PASS."
    else
        warn "Vulkan init FAIL — internal upscale se khong dung duoc."
        warn "Cai driver Vulkan theo GPU: sudo pacman -S --needed vulkan-radeon (AMD) / nvidia-utils (NVIDIA) / vulkan-intel (Intel)."
    fi
else
    warn "ffmpeg khong co libplacebo — kiem tra lai package ffmpeg."
fi

# 5. Tuỳ chọn (engine hoạt động không có, chỉ ảnh hưởng tính năng riêng):
#    - Upscayl (AI upscale ngoài): AUR `upscayl-bin` — không cài tự động qua AUR helper.
#    - whisper-cli (caption sync): build từ https://github.com/ggml-org/whisper.cpp rồi đặt
#      binary tại <app>/Assets/Whisper/whisper-cli. Thiếu thì caption bị skip (render vẫn xong).
#    - DLSS NGX (DLVSR.exe, VFX SDK): Windows-only — trên Linux engine tự log rồi rơi xuống
#      Upscayl/internal đúng như thiết kế.
say "Tuy chon (chua co thi bo qua tinh nang tuong ung):"
command -v upscayl-bin >/dev/null 2>&1 && say "  upscayl-bin: CO ($(command -v upscayl-bin))" || say "  upscayl-bin: CHUA (AUR: upscayl-bin)"
[ -x "$(pwd)/TitanEngine.Cli/bin/Release/net8.0/Assets/Whisper/whisper-cli" ] && say "  whisper-cli: CO" || say "  whisper-cli: CHUA (whisper.cpp — caption se bi skip)"

# 6. Kiểm tra nhanh bằng doctor nếu CLI đã build
CLI_BIN="$(pwd)/TitanEngine.Cli/bin/Release/net8.0/TitanEngine.Cli"
if [ -x "$CLI_BIN" ]; then
    say "CHAY DOCTOR KIEM TRA..."
    "$CLI_BIN" doctor || true
else
    say "CLI chua build — build bang: export PATH=\$HOME/.dotnet:\$PATH && dotnet build TitanEngine.Cli/TitanEngine.Cli.csproj -c Release"
fi

say "=========================================="
say "   SETUP HOAN TAT! (linux-x64 publish: dotnet publish TitanEngine.Cli -c Release -r linux-x64 --self-contained true)"
say "=========================================="
