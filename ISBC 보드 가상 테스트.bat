@echo off
rem ISBC board virtual test window (Renode + bundled Python/PySide6)
start "" "%~dp0python\pythonw.exe" "%~dp0isbc-renode\simulator\main.py"
