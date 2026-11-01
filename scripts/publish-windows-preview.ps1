param([string]$Output = ".artifacts/windows-x64")
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
$env:NUGET_HTTP_CACHE_PATH = Join-Path (Get-Location) ".cache/nuget-http"
$destination = [IO.Path]::GetFullPath($Output)
if (Test-Path $destination) { throw "El destino ya existe; use una carpeta nueva." }
New-Item -ItemType Directory -Path $destination | Out-Null
foreach ($item in @(
    @{ Project = "src/Agropecuaria.Desktop.Wpf"; Directory = "ui" },
    @{ Project = "src/Agropecuaria.LocalApi"; Directory = "backend" },
    @{ Project = "tools/Agropecuaria.Security.Cli"; Directory = "security" }
)) {
    dotnet publish $item.Project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishReadyToRun=false -o (Join-Path $destination $item.Directory)
    if ($LASTEXITCODE -ne 0) { throw "Error publicando $($item.Project)" }
}
@'
@echo off
cd /d "%~dp0"
"ui\Agropecuaria.Desktop.Wpf.exe" --preview
'@ | Set-Content -Encoding ascii (Join-Path $destination "Abrir-vista-previa.cmd")
@'
AGROPECUARIA CM v2 - VISTA PREVIA
1. Extrae toda la carpeta ZIP.
2. Abre Abrir-vista-previa.cmd.
Incluye el runtime .NET para Windows x64; no necesitas Visual Studio ni SDK.
Muestra la ventana WPF real con datos ficticios. No inicia una caja real.
No instala servicios ni modifica bases existentes.
La UI completa, login WPF, instalador y firma siguen pendientes.
El backend incluido es solo de desarrollo y mantiene el bloqueo de Production.
Guia y estado: https://github.com/alexmatzar/Agropecuaria-CM/tree/v2
'@ | Set-Content -Encoding utf8 (Join-Path $destination "LEEME.txt")
$version = (Get-Content VERSION -Raw).Trim()
@{ Version = $version; Runtime = "win-x64"; Mode = "VisualPreview"; SelfContained = $true } |
    ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $destination "manifest.json")
Write-Output "Paquete preparado: $destination"
