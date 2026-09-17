# Produce un singolo RfoGateManager.exe: nessun runtime .NET da installare,
# nessun installer. Accanto all'exe finiscono solo data\ e secrets\.
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $out = Join-Path $PSScriptRoot 'dist'

    dotnet publish src\RfoGateManager\RfoGateManager.csproj `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:PublishReadyToRun=true `
        -p:DebugType=none `
        -o $out `
        --nologo
    if ($LASTEXITCODE -ne 0) { throw "Pubblicazione fallita." }

    # Scarti della pubblicazione: l'utente deve trovare solo l'exe.
    Remove-Item (Join-Path $out '*.staticwebassets.endpoints.json'), (Join-Path $out 'web.config') -Force -ErrorAction SilentlyContinue

    # I file di lavoro viaggiano accanto all'exe, non dentro.
    New-Item -ItemType Directory -Force -Path (Join-Path $out 'data'), (Join-Path $out 'secrets') | Out-Null
    Copy-Item -Path (Join-Path $PSScriptRoot 'data\*') -Destination (Join-Path $out 'data') -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item -Path (Join-Path $PSScriptRoot 'secrets\booking.example.json') -Destination (Join-Path $out 'secrets') -Force -ErrorAction SilentlyContinue

    $exe = Get-Item (Join-Path $out 'RfoGateManager.exe')
    Write-Host ""
    Write-Host ("Pronto: {0} ({1:N1} MB)" -f $exe.FullName, ($exe.Length / 1MB))
    Write-Host "Distribuisci la cartella dist\ intera: exe + data\ + secrets\."
}
finally { Pop-Location }
