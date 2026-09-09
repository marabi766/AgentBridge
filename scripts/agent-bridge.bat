@echo off
rem Runs the headless Claude/Codex loop. Everything you type after the script
rem name is passed straight through, e.g.:
rem
rem   agent-bridge.bat --project "E:\MyRepo" --start-with codex --live
rem
rem -ExecutionPolicy Bypass is scoped to this one process: the loop has to be
rem startable from a scheduled task or a double click without the machine's
rem script policy having been changed for it.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0agent-bridge.ps1" %*
exit /b %ERRORLEVEL%
