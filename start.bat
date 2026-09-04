@echo off
chcp 65001 >nul
title 鸿蒙应用维护工具 - 启动器
cd /d "%~dp0"

set "EXE=publish\HarmonyFileManager.exe"

if /I "%~1"=="rebuild" goto build
if not exist "%EXE%" (
    echo [提示] 未找到正式版 %EXE%
    echo 请先运行: start.bat rebuild
    pause
    exit /b 1
)
goto run

:build
echo 正在打包正式版(自包含单文件)...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish -v q --nologo
if errorlevel 1 (
    echo.
    echo [提示] 打包失败,请查看上方错误信息。
    pause
    exit /b 1
)
if exist "publish\HarmonyFileManager.pdb" del "publish\HarmonyFileManager.pdb"
echo 打包完成。

:run
echo 正在启动 HarmonyFileManager ...
start "" "%EXE%"
exit /b 0
