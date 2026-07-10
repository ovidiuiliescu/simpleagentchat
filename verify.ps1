$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'tests\SimpleAgentChat.UnitTests\SimpleAgentChat.UnitTests.csproj'

dotnet build $project --nologo -warnaserror
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

dotnet run --no-build --project $project
exit $LASTEXITCODE
