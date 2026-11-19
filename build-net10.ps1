param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $BuildArgs
)

$sdkRoot = Join-Path $env:LOCALAPPDATA 'DeskLite\dotnet10'
$dotnet = Join-Path $sdkRoot 'dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    throw "Local .NET 10 SDK not found at '$dotnet'. Install it with dotnet-install.ps1 -Channel 10.0 -InstallDir '$sdkRoot'."
}

$env:DOTNET_ROOT = $sdkRoot
$isolatedOutput = Join-Path $PSScriptRoot 'bin\BuildOutput\'
& $dotnet build (Join-Path $PSScriptRoot 'DeskLite.csproj') -c Release "-p:BaseOutputPath=$isolatedOutput" @BuildArgs
exit $LASTEXITCODE
