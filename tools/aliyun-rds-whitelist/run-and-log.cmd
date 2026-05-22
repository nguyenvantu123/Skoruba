@echo off
REM Wrapper invoked by the Scheduled Task. Simpler than encoding redirection
REM into the task XML, which Task Scheduler can mangle.
setlocal
set "SCRIPT=%~dp0update-whitelist.ps1"
set "LOG=%~dp0update-whitelist.log"
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" >> "%LOG%" 2>&1
exit /b %errorlevel%
