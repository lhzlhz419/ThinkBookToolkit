param([ValidateSet("Debug", "Release")][string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$pluginProjectRoot = Split-Path -Parent $PSScriptRoot
$pluginProject = Join-Path $pluginProjectRoot "plugins\ThinkBookToolkit.PluginTest.Ui\ThinkBookToolkit.PluginTest.Ui.csproj"
& dotnet build $pluginProject -c $Configuration -m:1 --disable-build-servers
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$pluginBuildDirectory = Join-Path $pluginProjectRoot "plugins\ThinkBookToolkit.PluginTest\bin\$Configuration\net9.0"
$pluginUiBuildDirectory = Join-Path $pluginProjectRoot "plugins\ThinkBookToolkit.PluginTest.Ui\bin\$Configuration\net9.0-windows"
$pluginPackageDirectory = Join-Path $pluginProjectRoot "dist\test-plugins\PluginTest"
New-Item -ItemType Directory -Path $pluginPackageDirectory -Force | Out-Null
foreach ($pluginFile in @("ThinkBookToolkit.PluginTest.dll", "ThinkBookToolkit.PluginTest.deps.json", "plugin.json")) {
    Copy-Item -LiteralPath (Join-Path $pluginBuildDirectory $pluginFile) -Destination (Join-Path $pluginPackageDirectory $pluginFile) -Force
}
foreach ($pluginFile in @("ThinkBookToolkit.PluginTest.Ui.dll", "ThinkBookToolkit.PluginTest.Ui.deps.json")) {
    Copy-Item -LiteralPath (Join-Path $pluginUiBuildDirectory $pluginFile) -Destination (Join-Path $pluginPackageDirectory $pluginFile) -Force
}
Write-Host "Standalone test plugin: $pluginPackageDirectory"
$pluginArchive = Join-Path $pluginProjectRoot "dist\test-plugins\ThinkBookToolkit.PluginTest.zip"
Compress-Archive -LiteralPath $pluginPackageDirectory -DestinationPath $pluginArchive -Force
Write-Host "Standalone test archive: $pluginArchive"
Write-Host "Import this ZIP from Toolkit's Plugins page. Review sensors.read and ui.custom before enabling. Updates to loaded UI DLLs require a restart. This sample is not part of the application installer."
