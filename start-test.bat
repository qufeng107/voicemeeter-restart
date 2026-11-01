@echo off
cd /d "%~dp0"
py autorestart-audio-engine.py --diagnostic
pause
