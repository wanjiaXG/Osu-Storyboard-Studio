@echo off
rem Builds the latest Release version and starts it, so the running program always matches the source.
chcp 65001 >nul
cd /d "%~dp0"

rem A running copy keeps its files locked and the build could not replace them.
tasklist /FI "IMAGENAME eq OsuStoryboardStudio.exe" 2>nul | find /I "OsuStoryboardStudio.exe" >nul
if not errorlevel 1 (
    echo 程序窗口还开着。请先关闭它,然后重新运行本脚本,这样才能换成最新版本。
    pause
    exit /b 1
)

echo 正在编译最新版本...
dotnet build src\SbStudio.App -c Release --nologo -v q
if errorlevel 1 (
    echo.
    echo 编译没有完成,请查看上面的错误信息。
    pause
    exit /b 1
)
start "" "src\SbStudio.App\bin\Release\net10.0\OsuStoryboardStudio.exe"
