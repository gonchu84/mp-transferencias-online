#requires -Version 5.1
<#
Instalador simple:
- Crea accesos directos en Escritorio
- Agrega regla de firewall para el puerto del config (solo perfil Private)
#>

$ErrorActionPreference = "Stop"

$Base = Split-Path -Parent $MyInvocation.MyCommand.Path
$RunBat = Join-Path $Base "run.bat"
$OpenUrl = Join-Path $Base "Abrir MP.url"
$ConfigPath = Join-Path $Base "config\appsettings.Sucursal.json"

if (!(Test-Path $RunBat)) { throw "No existe run.bat" }
if (!(Test-Path $OpenUrl)) { throw "No existe Abrir MP.url" }
if (!(Test-Path $ConfigPath)) { throw "No existe config\appsettings.Sucursal.json" }

# Leer puerto de Hosting:Urls (ej: http://localhost:5286)
$configJson = Get-Content $ConfigPath -Raw | ConvertFrom-Json
$urls = $configJson.Hosting.Urls
if (-not $urls) { $urls = "http://localhost:5286" }

# Extraer puerto
$port = 5286
if ($urls -match ":(\d+)") { $port = [int]$matches[1] }

$desktop = [Environment]::GetFolderPath("Desktop")

# Crear shortcuts (.lnk)
$wsh = New-Object -ComObject WScript.Shell

function New-Shortcut($path, $target, $args, $workdir, $icon) {
  $sc = $wsh.CreateShortcut($path)
  $sc.TargetPath = $target
  if ($args) { $sc.Arguments = $args }
  if ($workdir) { $sc.WorkingDirectory = $workdir }
  if ($icon) { $sc.IconLocation = $icon }
  $sc.Save()
}

$lnkStart = Join-Path $desktop "MP Transferencias - Iniciar.lnk"
New-Shortcut -path $lnkStart -target "cmd.exe" -args "/c `"$RunBat`"" -workdir $Base -icon "$env:SystemRoot\System32\SHELL32.dll,167"

Copy-Item $OpenUrl (Join-Path $desktop "MP Transferencias - Abrir.url") -Force

# Firewall rule (Private)
$ruleName = "MP Transferencias Local ($port)"
# Eliminar si existe
Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue | Out-Null

New-NetFirewallRule `
  -DisplayName $ruleName `
  -Direction Inbound `
  -Action Allow `
  -Protocol TCP `
  -LocalPort $port `
  -Profile Private `
  | Out-Null

Write-Host "OK. Accesos directos creados en Escritorio."
Write-Host "Firewall OK para puerto $port (perfil Private)."
Write-Host ""
Write-Host "Siguiente: asegurate de haber hecho publish dentro de la carpeta app\"
Write-Host "Luego ejecuta 'MP Transferencias - Iniciar' y abre la URL."
