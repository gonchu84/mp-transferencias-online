@echo off
setlocal
cd /d "%~dp0"

REM Levanta el servidor en segundo plano
start "" /min "%~dp0run.bat"

REM Espera 2 segundos
timeout /t 2 /nobreak >nul

REM Abre el navegador
start "" "http://localhost:5286"
