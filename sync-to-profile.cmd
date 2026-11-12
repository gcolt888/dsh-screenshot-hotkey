@echo off
rem Sync this repo into the local DSH desktop profile.
rem
rem DSH resolves the plugin by package name from the profile's node_modules, and a
rem junction there was measured to stall client boot -- so the runtime copies must be
rem real directories. Both of them come from this repo; run this after editing.
rem
rem Optional: set DSH_HOME before running if your DSH home is not E:\dsh-home.
rem
rem 2026-11-12 FIXED: %~dp0 ends with a backslash, and robocopy reads "path\" as an
rem escaped quote -- it then treats both paths plus the switches as ONE argument and
rem dies with "No Destination Directory Specified" (exit 16). The old version sent
rem that output to nul and printed "Synced" anyway, so it never copied anything.
rem This is how the runtime copy stayed broken after "re-running the sync".
setlocal
set "SRC=%~dp0"
if "%SRC:~-1%"=="\" set "SRC=%SRC:~0,-1%"
if "%DSH_HOME%"=="" set "DSH_HOME=E:\dsh-home"
set "PROFILE=%DSH_HOME%\profiles\desktop"

if not exist "%PROFILE%" (
  echo DSH profile not found: %PROFILE%
  exit /b 1
)

robocopy "%SRC%" "%PROFILE%\local-plugins\dsh-screenshot-hotkey" /E /XD .git node_modules /NP /NJS
if errorlevel 8 goto fail
robocopy "%SRC%" "%PROFILE%\node_modules\dsh-screenshot-hotkey" /E /XD .git node_modules /NP /NJS
if errorlevel 8 goto fail

echo Synced to %PROFILE%
echo Restart DSH to pick up changes.
exit /b 0

:fail
echo Sync FAILED (robocopy exit %errorlevel%), nothing is guaranteed in place.
exit /b 1
