@echo off
echo === VoSiBot - Build single EXE ===
cd /d "%~dp0"

dotnet publish VoSiBot/VoSiBot.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish

if exist publish\VoSiBot.exe (
    echo.
    echo [OK] Da tao xong: publish\VoSiBot.exe
    echo      Copy file nay sang may moi la chay duoc!
    explorer publish
) else (
    echo [LOI] Build that bai, kiem tra dotnet SDK da cai chua.
)
pause
