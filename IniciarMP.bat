@echo off
title MP Transferencias - PS3 Larroque

echo ================================
echo Iniciando MP Transferencias
echo ================================

cd /d "%~dp0src\MpTransferenciasLocal"

dotnet run

pause
