@echo off
chcp 65001 >nul
cd /d "%~dp0"
python -X utf8 "%~dp0campus-offline-diagnostics.py" --vpn-auth %*
if errorlevel 1 pause
