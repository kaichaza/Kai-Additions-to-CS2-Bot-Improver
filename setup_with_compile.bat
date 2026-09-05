@echo off
rem ===========================================================================
rem setup_with_compile.bat  -  KaiBotTactics installer, COMPILES FROM SOURCE
rem
rem This script COMPILES the plugin before installing it. Use it for a fresh
rem build from the C# sources in this repository; if you just want to install
rem the prebuilt DLL that ships here, use setup_no_compile.bat instead.
rem
rem What it does, in order:
rem
rem   1. reads the game location from counterstrike_location.txt (this folder)
rem   2. checks Metamod / CounterStrikeSharp / CS2-Bot-Improver are in place
rem   3. fetches the two shared reference assemblies the build needs
rem      (BotControllerApi.dll, RayTraceApi.dll) from the installed game into
rem      libs\ next to the csproj, if they are not already there
rem   4. dotnet build -c Release  (needs the .NET 10.0 SDK on PATH)
rem   5. refreshes the repository's prebuilt payload at
rem      game\csgo\addons\counterstrikesharp\plugins\KaiBotTactics\ with the freshly built DLL
rem   6. copies the plugin, its data folder, and gungame_pro.cfg into the game
rem
rem Learned data already present in the game is NOT overwritten by older
rem files from this repository (robocopy /XO), so re-running this script
rem after playtests will not clobber what your bots have learned.
rem ===========================================================================

setlocal EnableExtensions
set "HERE=%~dp0"
set "LOCFILE=%HERE%counterstrike_location.txt"

echo.
echo === KaiBotTactics setup (compile from source) ===
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
    goto :fail
)

if not exist "%CSS%\" (
    echo [ERROR] CounterStrikeSharp is not installed at
    echo         game\csgo\addons\counterstrikesharp.
    goto :fail
)

if not exist "%CSS%\shared\BotControllerApi\BotControllerApi.dll" (
    echo [ERROR] CS2-Bot-Improver does not appear to be installed:
    echo         shared\BotControllerApi\BotControllerApi.dll is missing.
    goto :fail
)

echo Prerequisites found: Metamod, CounterStrikeSharp, CS2-Bot-Improver.

rem --- 3. Shared reference assemblies into libs\ ---------------------------
rem Referenced with Private=false: the runtime already has both loaded, and a
rem second copy in the output would create two distinct types with the same
rem names, which silently breaks the capability lookups. They are needed at
rem BUILD time only, which is why they live in libs\ and never in the output.
if not exist "%HERE%libs\" mkdir "%HERE%libs"

if not exist "%HERE%libs\BotControllerApi.dll" (
    echo Fetching BotControllerApi.dll from the installed game into libs\
    copy /Y "%CSS%\shared\BotControllerApi\BotControllerApi.dll" "%HERE%libs\" >nul
    if errorlevel 1 (
        echo [ERROR] Could not copy BotControllerApi.dll into libs\.
        goto :fail
    )
)

if not exist "%HERE%libs\RayTraceApi.dll" (
    if exist "%CSS%\shared\RayTraceApi\RayTraceApi.dll" (
        echo Fetching RayTraceApi.dll from the installed game into libs\
        copy /Y "%CSS%\shared\RayTraceApi\RayTraceApi.dll" "%HERE%libs\" >nul
    ) else (
        echo [ERROR] RayTraceApi.dll not found in the game's shared folder.
        echo         Check your CS2-Bot-Improver installation.
        goto :fail
    )
)

rem --- 4. Build -------------------------------------------------------------
where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] The 'dotnet' command was not found. Install the .NET 10.0 SDK
    echo         from dot.net and run this script again.
    goto :fail
)

echo Building KaiBotTactics ^(Release^)...
pushd "%HERE%"
dotnet build -c Release
if errorlevel 1 (
    popd
    echo [ERROR] Build failed. Read the compiler output above.
    goto :fail
)
popd

set "BUILT=%HERE%bin\Release\net10.0"

if not exist "%BUILT%\KaiBotTactics.dll" (
    echo [ERROR] Build reported success but %BUILT%\KaiBotTactics.dll
    echo         was not found. Check the csproj AssemblyName.
    goto :fail
)

rem --- 5. Refresh the repository's prebuilt payload ------------------------
set "SRC=%HERE%game\csgo\addons\counterstrikesharp\plugins\KaiBotTactics"
if not exist "%SRC%\" mkdir "%SRC%"

copy /Y "%BUILT%\KaiBotTactics.dll" "%SRC%\" >nul
if exist "%BUILT%\KaiBotTactics.deps.json" copy /Y "%BUILT%\KaiBotTactics.deps.json" "%SRC%\" >nul

echo Freshly built DLL staged at game\csgo\addons\counterstrikesharp\plugins\KaiBotTactics\

rem --- 6. Copy into the game -----------------------------------------------
set "DST=%CSS%\plugins\KaiBotTactics"

echo Copying plugin and learned data to:
echo   %DST%
rem /E copies subfolders including empty ones (logs\).
rem /XO skips files OLDER than the destination copy, protecting learned data.
robocopy "%SRC%" "%DST%" /E /XO /NFL /NDL /NJH /NJS
if %ERRORLEVEL% GEQ 8 (
    echo [ERROR] robocopy reported a failure copying the plugin.
    goto :fail
)

rem The DLL itself must always be the one just built, even if the destination
rem copy has a newer timestamp from an earlier run, so it is copied again
rem explicitly without the age test.
copy /Y "%SRC%\KaiBotTactics.dll" "%DST%\" >nul
if exist "%SRC%\KaiBotTactics.deps.json" copy /Y "%SRC%\KaiBotTactics.deps.json" "%DST%\" >nul

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
echo If the game was already running, reload with:
echo   css_plugins reload KaiBotTactics
echo.
pause
exit /b 0

:fail
echo.
echo Setup did not complete.
pause
exit /b 1
