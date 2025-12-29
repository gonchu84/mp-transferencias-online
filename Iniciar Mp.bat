@echo off
title MP Transferencias - PS3 Larroque

echo ================================
echo Iniciando MP Transferencias
echo ================================

cd /d "%~dp0src\MpTransferenciasLocal"

REM Arranca la app minimizada
start "MP Transferencias" /min cmd /c dotnet run

REM Espera a que levante el servidor
timeout /t 5 /nobreak > nul

REM Abre el navegador (predeterminado)
start http://localhost:5286

exit
