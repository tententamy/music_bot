@echo off
echo === VoSiBot - Build single EXE ===
cd /d "%~dp0"

echo [1/4] Dong VoSiBot dang chay (neu co)...
taskkill /f /im VoSiBot.exe >nul 2>&1
timeout /t 2 /nobreak >nul

echo [2/4] Build vao thu muc tam...
if exist publish_tmp rmdir /s /q publish_tmp

dotnet publish VoSiBot/VoSiBot.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o publish_tmp

if %errorlevel% neq 0 (
    echo.
    echo [LOI] Build that bai! Xem loi o tren.
    pause
    exit /b 1
)

echo [3/4] Copy sang publish\...
if not exist publish mkdir publish
copy /y publish_tmp\VoSiBot.exe publish\VoSiBot.exe >nul
if %errorlevel% neq 0 (
    echo.
    echo [LOI] Khong copy duoc - file dang bi giu boi tien trinh khac.
    echo       Hay tat VoSiBot.exe roi chay lai.
    pause
    exit /b 1
)

echo [4/4] Don dep...
rmdir /s /q publish_tmp

echo.
echo [OK] Da tao xong: publish\VoSiBot.exe
echo      Copy file nay sang may moi la chay duoc!
explorer publish
pause
