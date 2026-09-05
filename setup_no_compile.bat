@echo off
rem ===========================================================================
rem setup_no_compile.bat  -  KaiBotTactics installer, PREBUILT, NO COMPILING
rem
rem This script does NOT compile anything. It copies the prebuilt plugin that
rem ships in this repository straight into your Counter-Strike installation:
rem
rem   1. reads the game location from counterstrike_location.txt (this folder)
rem   2. checks Metamod / CounterStrikeSharp / CS2-Bot-Improver are in place
rem   3. copies game\csgo\addons\counterstrikesharp\plugins\KaiBotTactics\  (KaiBotTactics.dll,
rem      KaiBotTactics.deps.json, and the kai_tactics data folder with the
rem      learned per-map files) into
rem      <game>\game\csgo\addons\counterstrikesharp\plugins\KaiBotTactics\
rem   4. copies game\csgo\cfg\gungame_pro.cfg into <game>\game\csgo\cfg\
rem
rem If you are already running ed0ard's CS2-Bot-Improver, this is the whole
rem installation: the DLL and its dependencies are prebuilt and load alongside
rem his plugins as-is. Use setup_with_compile.bat instead only if you want to
rem build the DLL from source yourself.
rem
rem Learned data already present in the game is NOT overwritten by older
rem files from this repository (robocopy /XO), so re-running this script
rem after playtests will not clobber what your bots have learned.
rem ===========================================================================

setlocal EnableExtensions
set "HERE=%~dp0"
set "LOCFILE=%HERE%counterstrike_location.txt"

echo.
echo === KaiBotTactics setup (prebuilt, no compiling) ===
echo.

rem --- 1. Read the game location -------------------------------------------
if not exist "%LOCFILE%" (
    echo [ERROR] counterstrike_location.txt not found next to this script.
    echo         Create it and put your game path inside. See readme.md.
    goto :fail
)

set "CSLOC="
for /f "usebackq eol=# delims=" %%A in ("%LOCFILE%") do (
    if not defined CSLOC set "CSLOC=%%A"
)

if not defined CSLOC (
    echo [ERROR] counterstrike_location.txt contains no path.
    echo         The first line that is not a '#' comment must be the game path.
    goto :fail
)

echo Game location: "%CSLOC%"

if not exist "%CSLOC%\game\csgo\" (
    echo [ERROR] "%CSLOC%\game\csgo" does not exist.
    echo         The path must point at the folder that CONTAINS the 'game'
    echo         directory. Fix counterstrike_location.txt and run again.
    goto :fail
)

rem --- 2. Check the stack underneath is installed --------------------------
set "CSS=%CSLOC%\game\csgo\addons\counterstrikesharp"

if not exist "%CSLOC%\game\csgo\addons\metamod\" (
    echo [ERROR] Metamod:Source is not installed at game\csgo\addons\metamod.
    echo         Install Metamod first. See the Installation section of readme.md.
    goto :fail
)

if not exist "%CSS%\" (
    echo [ERROR] CounterStrikeSharp is not installed at
    echo         game\csgo\addons\counterstrikesharp. Install it first.
    goto :fail
)

if not exist "%CSS%\shared\BotControllerApi\BotControllerApi.dll" (
    echo [ERROR] CS2-Bot-Improver does not appear to be installed:
    echo         shared\BotControllerApi\BotControllerApi.dll is missing.
    echo         KaiBotTactics extends ed0ard's plugin and needs it present.
    goto :fail
)

echo Prerequisites found: Metamod, CounterStrikeSharp, CS2-Bot-Improver.
echo.

rem --- 3. Copy the prebuilt plugin and its data ----------------------------
set "SRC=%HERE%game\csgo\addons\counterstrikesharp\plugins\KaiBotTactics"
set "DST=%CSS%\plugins\KaiBotTactics"

if not exist "%SRC%\KaiBotTactics.dll" (
    echo [ERROR] Prebuilt plugin not found at:
    echo         %SRC%\KaiBotTactics.dll
    echo         This repository copy is incomplete, or you wanted
    echo         setup_with_compile.bat instead.
    goto :fail
)

echo Copying plugin and learned data to:
echo   %DST%
rem /E copies subfolders including empty ones (logs\).
rem /XO skips files that are OLDER than what is already at the destination,
rem so a re-run never overwrites newer learned data with repository copies.
robocopy "%SRC%" "%DST%" /E /XO /NFL /NDL /NJH /NJS
if %ERRORLEVEL% GEQ 8 (
    echo [ERROR] robocopy reported a failure copying the plugin.
    goto :fail
)

rem --- 4. Copy the mode config ---------------------------------------------
if exist "%HERE%game\csgo\cfg\gungame_pro.cfg" (
    echo Copying gungame_pro.cfg to game\csgo\cfg\
    copy /Y "%HERE%game\csgo\cfg\gungame_pro.cfg" "%CSLOC%\game\csgo\cfg\" >nul
    if errorlevel 1 (
        echo [ERROR] Could not copy gungame_pro.cfg.
        goto :fail
    )
) else (
    echo [WARN] game\csgo\cfg\gungame_pro.cfg not found in this repository;
    echo        skipping the mode config. The plugin will still load.
)

echo.
echo === Done. ===
echo Start a map with bots. The plugin loads automatically and executes
echo gungame_pro.cfg a few seconds after map load - the prohibited items
echo printing by name in the console is the sign it worked. Verify with:
echo   css_plugins list
echo   kai_maturity
echo.
pause
exit /b 0

:fail
echo.
echo Setup did not complete. Nothing further was changed.
pause
exit /b 1
