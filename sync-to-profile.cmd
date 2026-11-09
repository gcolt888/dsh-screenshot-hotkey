@echo off
rem Sync this repo into the local DSH desktop profile.
rem
rem DSH resolves the plugin by package name from the profile's node_modules, and a
rem junction there was measured to stall client boot -- so the runtime copies must be
rem real directories. Both of them come from this repo; run this after editing.
rem
rem Optional: set DSH_HOME before running if your DSH home is not E:\dsh-home.
setlocal
set "SRC=%~dp0"
if "%DSH_HOME%"=="" set "DSH_HOME=E:\dsh-home"
set "PROFILE=%DSH_HOME%\profiles\desktop"

if not exist "%PROFILE%" (
  echo DSH profile not found: %PROFILE%
  exit /b 1
)

robocopy "%SRC%" "%PROFILE%\local-plugins\dsh-screenshot-hotkey" /E /XD .git node_modules >nul
robocopy "%SRC%" "%PROFILE%\node_modules\dsh-screenshot-hotkey" /E /XD .git node_modules >nul

echo Synced to %PROFILE%
echo Restart DSH to pick up changes.
