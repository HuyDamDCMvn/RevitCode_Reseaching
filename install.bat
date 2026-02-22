@echo off
chcp 65001 >nul 2>&1
setlocal enabledelayedexpansion

:: ============================================================================
:: HD Extension - Installer
:: ============================================================================
:: Double-click to install HD.extension into pyRevit extensions folder.
:: ============================================================================

title HD Extension - Installer

echo ============================================
echo   HD Extension - Installer
echo ============================================
echo.

:: ---------------------------------------------------------------------------
:: 1. Check HD.extension folder exists next to this script
:: ---------------------------------------------------------------------------
set "SCRIPT_DIR=%~dp0"
set "SOURCE_DIR=%SCRIPT_DIR%HD.extension"

if not exist "%SOURCE_DIR%\" (
    echo [ERROR] HD.extension folder not found.
    echo         Place install.bat alongside the HD.extension folder.
    echo.
    pause
    exit /b 1
)

:: Read version info if available
if exist "%SOURCE_DIR%\VERSION.txt" (
    set /p VERSION_INFO=<"%SOURCE_DIR%\VERSION.txt"
    echo   Version: !VERSION_INFO!
    echo.
)

:: ---------------------------------------------------------------------------
:: 2. Find pyRevit extensions folder
:: ---------------------------------------------------------------------------
set "PYREVIT_EXT=%APPDATA%\pyRevit-Master\extensions"

:: Check if pyRevit extensions folder exists
if not exist "%PYREVIT_EXT%\" (
    :: Try alternative path
    set "PYREVIT_EXT=%APPDATA%\pyRevit\extensions"
)

if not exist "%PYREVIT_EXT%\" (
    echo [ERROR] pyRevit extensions folder not found.
    echo.
    echo   Checked:
    echo     - %APPDATA%\pyRevit-Master\extensions\
    echo     - %APPDATA%\pyRevit\extensions\
    echo.
    echo   Make sure pyRevit is installed.
    echo   Or manually copy HD.extension into your pyRevit extensions folder.
    echo.
    pause
    exit /b 1
)

echo [1/3] pyRevit folder: %PYREVIT_EXT%

:: ---------------------------------------------------------------------------
:: 3. Backup existing installation (if any)
:: ---------------------------------------------------------------------------
set "DEST_DIR=%PYREVIT_EXT%\HD.extension"

if exist "%DEST_DIR%\" (
    echo [2/3] Backing up existing installation...

    :: Remove old backup if exists
    if exist "%DEST_DIR%.backup\" (
        rmdir /s /q "%DEST_DIR%.backup" >nul 2>&1
    )

    :: Rename current to backup
    rename "%DEST_DIR%" "HD.extension.backup" >nul 2>&1
    if errorlevel 1 (
        echo [WARNING] Could not back up existing version. Attempting to remove...
        rmdir /s /q "%DEST_DIR%" >nul 2>&1
        if errorlevel 1 (
            echo [ERROR] Could not remove existing version. Please close Revit and try again.
            echo.
            pause
            exit /b 1
        )
    ) else (
        echo          Backup: HD.extension.backup
    )
) else (
    echo [2/3] Fresh install (no previous version found)
)

:: ---------------------------------------------------------------------------
:: 4. Copy HD.extension to pyRevit extensions
:: ---------------------------------------------------------------------------
echo [3/3] Installing...

xcopy "%SOURCE_DIR%" "%DEST_DIR%\" /e /i /q /y >nul 2>&1
if errorlevel 1 (
    echo.
    echo [ERROR] Installation failed. Could not copy files.
    echo.
    :: Try to restore backup
    if exist "%PYREVIT_EXT%\HD.extension.backup\" (
        echo Restoring backup...
        rename "%PYREVIT_EXT%\HD.extension.backup" "HD.extension" >nul 2>&1
    )
    pause
    exit /b 1
)

:: ---------------------------------------------------------------------------
:: 5. Success
:: ---------------------------------------------------------------------------
echo.
echo ============================================
echo   INSTALLATION SUCCESSFUL!
echo ============================================
echo.
echo   Installed to: %DEST_DIR%
echo.
if exist "%PYREVIT_EXT%\HD.extension.backup\" (
    echo   Previous version backed up: HD.extension.backup
    echo   (You can delete the backup after verifying the new version works)
    echo.
)

:: ---------------------------------------------------------------------------
:: 6. AI Chatbot (Ollama) setup
:: ---------------------------------------------------------------------------
echo ============================================
echo   AI Chatbot Setup (RevitChatLocal)
echo ============================================
echo.

where ollama >nul 2>&1
if !errorlevel! equ 0 (
    echo   Ollama: INSTALLED
    echo.
    set /p PULL_MODEL="   Download AI model qwen2.5:7b now? (~4GB) [Y/N]: "
    if /i "!PULL_MODEL!"=="Y" (
        echo.
        echo   Downloading model... (this may take 5-15 minutes)
        echo.
        ollama pull qwen2.5:7b
        if !errorlevel! equ 0 (
            echo.
            echo   Model downloaded successfully!
        ) else (
            echo.
            echo   [WARNING] Model download failed. You can retry later:
            echo             ollama pull qwen2.5:7b
        )
    ) else (
        echo.
        echo   Skipped. To download later, open a terminal and run:
        echo     ollama pull qwen2.5:7b
    )
) else (
    echo   Ollama: NOT FOUND
    echo.
    echo   To use the AI chatbot (RevitChatLocal), you need:
    echo     1. Install Ollama from https://ollama.com
    echo     2. Open a terminal and run: ollama pull qwen2.5:7b
    echo     3. Restart Revit
    echo.
    echo   (Other tools like SmartTag, CommonFeature work without Ollama)
)

echo.
echo ============================================
echo   Next Steps
echo ============================================
echo.
echo     - Open Revit and reload pyRevit (pyRevit tab ^> Reload)
echo     - Or restart Revit
echo     - HD tab ^> AI ^> RevitChatLocal to open chatbot
echo.
pause
