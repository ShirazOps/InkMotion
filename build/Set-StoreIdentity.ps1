<#
.SYNOPSIS
Applies the Partner Center identity to Package.appxmanifest for a Store build.

.DESCRIPTION
The checked-in manifest holds placeholders so that Microsoft Store details stay out of source control.
CI passes the values from repository secrets through environment variables. For a local Store build, set
the same variables in your session, run this script, and then discard the manifest change
(git checkout -- src/InkMotion/Package.appxmanifest) so the identity is never committed.

Required: STORE_PACKAGE_NAME, STORE_PUBLISHER, STORE_PUBLISHER_DISPLAY_NAME.
Optional: STORE_DISPLAY_NAME (defaults to the manifest's display name).
#>
[CmdletBinding()]
param(
    [string]$ManifestPath = (Join-Path $PSScriptRoot '..\src\InkMotion\Package.appxmanifest')
)

$ErrorActionPreference = 'Stop'

$values = [ordered]@{
    STORE_PACKAGE_NAME           = $env:STORE_PACKAGE_NAME
    STORE_PUBLISHER              = $env:STORE_PUBLISHER
    STORE_PUBLISHER_DISPLAY_NAME = $env:STORE_PUBLISHER_DISPLAY_NAME
}
$missing = @($values.Keys | Where-Object { [string]::IsNullOrWhiteSpace($values[$_]) })
if ($missing.Count -gt 0) {
    throw "Missing Store identity values: $($missing -join ', '). Add them as repository secrets."
}

if ($values.STORE_PACKAGE_NAME -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]{2,49}$') {
    throw 'STORE_PACKAGE_NAME is not a valid package name.'
}

if ($values.STORE_PUBLISHER -notmatch '^CN=') {
    throw 'STORE_PUBLISHER must be the full publisher value from Partner Center, starting with CN=.'
}

$path = (Resolve-Path $ManifestPath).Path
$manifest = New-Object System.Xml.XmlDocument
$manifest.PreserveWhitespace = $true
$manifest.Load($path)
$namespaces = New-Object System.Xml.XmlNamespaceManager($manifest.NameTable)
$namespaces.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$namespaces.AddNamespace('uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')

$identity = $manifest.SelectSingleNode('/f:Package/f:Identity', $namespaces)
if ($identity.GetAttribute('Version') -notmatch '^\d+\.\d+\.\d+\.0$') {
    throw "Store package versions must end in .0 (found $($identity.GetAttribute('Version')))."
}

$identity.SetAttribute('Name', $values.STORE_PACKAGE_NAME)
$identity.SetAttribute('Publisher', $values.STORE_PUBLISHER)
$manifest.SelectSingleNode('/f:Package/f:Properties/f:PublisherDisplayName', $namespaces).InnerText = $values.STORE_PUBLISHER_DISPLAY_NAME

if (-not [string]::IsNullOrWhiteSpace($env:STORE_DISPLAY_NAME)) {
    $manifest.SelectSingleNode('/f:Package/f:Properties/f:DisplayName', $namespaces).InnerText = $env:STORE_DISPLAY_NAME
    foreach ($element in $manifest.SelectNodes('//uap:VisualElements', $namespaces)) {
        $element.SetAttribute('DisplayName', $env:STORE_DISPLAY_NAME)
    }
}

$writer = New-Object System.IO.StreamWriter($path, $false, (New-Object System.Text.UTF8Encoding($false)))
try {
    $manifest.Save($writer)
}
finally {
    $writer.Dispose()
}

Write-Host 'Applied the Store identity to Package.appxmanifest.'
