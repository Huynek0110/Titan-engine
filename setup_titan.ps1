# =============================================================================
# TITAN ENGINE AUTO-SETUP SCRIPT
# Tự động tải FFmpeg và cài đặt vào Project
# =============================================================================

Write-Host ">>> DANG KHOI DONG TITAN SETUP..." -ForegroundColor Cyan

# 1. Xác định đường dẫn Project (Nơi chứa file .csproj)
# Script này giả định bạn để nó cùng cấp với file .sln
$projectPath = Get-Location
$binPath = Join-Path $projectPath "TitanEngineV60\bin\Debug\net8.0-windows"

# Tạo thư mục bin nếu chưa có (để tránh lỗi nếu chưa build lần nào)
if (-not (Test-Path $binPath)) {
    Write-Host ">>> Tao thu muc dich: $binPath" -ForegroundColor Yellow
    New-Item -ItemType Directory -Force -Path $binPath | Out-Null
}

# 2. Link tải FFmpeg (Bản build chuẩn của Gyan.dev)
$ffmpegUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
$zipFile = Join-Path $projectPath "ffmpeg.zip"
$extractPath = Join-Path $projectPath "ffmpeg_temp"

# 3. Tải về
Write-Host ">>> DANG TAI FFMPEG (Khoang 30-50MB)... Vui long doi..." -ForegroundColor Green
try {
    Invoke-WebRequest -Uri $ffmpegUrl -OutFile $zipFile
    Write-Host ">>> Tai xong!" -ForegroundColor Green
}
catch {
    Write-Host ">>> LOI KHI TAI: $_" -ForegroundColor Red
    Write-Host "Kiem tra mang hoac link tai."
    Read-Host "Nhan Enter de thoat..."
    exit
}

# 4. Giải nén
Write-Host ">>> DANG GIAI NEN..." -ForegroundColor Yellow
Expand-Archive -LiteralPath $zipFile -DestinationPath $extractPath -Force

# 5. Tìm và Copy file exe vào Project
Write-Host ">>> DANG CAI DAT VAO PROJECT..." -ForegroundColor Yellow
$ffmpegExe = Get-ChildItem -Path $extractPath -Recurse -Filter "ffmpeg.exe" | Select-Object -First 1
$ffprobeExe = Get-ChildItem -Path $extractPath -Recurse -Filter "ffprobe.exe" | Select-Object -First 1

if ($ffmpegExe -and $ffprobeExe) {
    Copy-Item -Path $ffmpegExe.FullName -Destination $binPath -Force
    Copy-Item -Path $ffprobeExe.FullName -Destination $binPath -Force
    Write-Host ">>> DA CHEP FFMPEG.EXE VAO: $binPath" -ForegroundColor Cyan
    Write-Host ">>> DA CHEP FFPROBE.EXE VAO: $binPath" -ForegroundColor Cyan
} else {
    Write-Host ">>> LOI: Khong tim thay file exe trong goi tai ve!" -ForegroundColor Red
}

# 6. Dọn dẹp rác
Write-Host ">>> DON DEP FILE TAM..." -ForegroundColor Yellow
Remove-Item -Path $zipFile -Force
Remove-Item -Path $extractPath -Recurse -Force

Write-Host "==========================================" -ForegroundColor Green
Write-Host "   SETUP HOAN TAT! SAN SANG CODE C#" -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Green
Read-Host "Nhan Enter de ket thuc..."