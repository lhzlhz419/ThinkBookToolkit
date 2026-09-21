param([ValidateSet("Debug", "Release")][string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$pluginProjectRoot = Split-Path -Parent $PSScriptRoot
$pluginProject = Join-Path $pluginProjectRoot "plugins\ThinkBookToolkit.PluginTest\ThinkBookToolkit.PluginTest.csproj"
& dotnet build $pluginProject -c $Configuration -m:1 --disable-build-servers
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$pluginBuildDirectory = Join-Path $pluginProjectRoot "plugins\ThinkBookToolkit.PluginTest\bin\$Configuration\net9.0"
$pluginPackageDirectory = Join-Path $pluginProjectRoot "dist\test-plugins\PluginTest"
New-Item -ItemType Directory -Path $pluginPackageDirectory -Force | Out-Null
foreach ($pluginFile in @("ThinkBookToolkit.PluginTest.dll", "ThinkBookToolkit.PluginTest.deps.json", "plugin.json")) {
    Copy-Item -LiteralPath (Join-Path $pluginBuildDirectory $pluginFile) -Destination (Join-Path $pluginPackageDirectory $pluginFile) -Force
}
Write-Host "Standalone test plugin: $pluginPackageDirectory"
$pluginArchive = Join-Path $pluginProjectRoot "dist\test-plugins\ThinkBookToolkit.PluginTest.zip"
Compress-Archive -LiteralPath $pluginPackageDirectory -DestinationPath $pluginArchive -Force
Write-Host "Standalone test archive: $pluginArchive"
Write-Host "Copy this folder into the plugin directory shown by Toolkit, then restart and approve the plugin. It is not part of the application installer."
