# Esegue la suite. dotnet test non aggancia il runner Microsoft.Testing.Platform
# su questo SDK, quindi lanciamo direttamente l'eseguibile dei test.
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet build tests\RfoGateManager.Tests\RfoGateManager.Tests.csproj --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Compilazione dei test fallita." }

    & .\tests\RfoGateManager.Tests\bin\Debug\net10.0\RfoGateManager.Tests.exe @args
    exit $LASTEXITCODE
}
finally { Pop-Location }
