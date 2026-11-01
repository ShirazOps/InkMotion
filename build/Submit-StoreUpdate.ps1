[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$ReleaseNotesPath,
    [ValidateRange(1, 120)][int]$PollAttempts = 40,
    [ValidateRange(0, 120)][int]$PollSeconds = 15
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-StoreRequest {
    param([string]$Method, [string]$Path, [hashtable]$Body)
    $parameters = @{
        Method = $Method
        Uri = "$script:api/$Path"
        Headers = @{ Authorization = "Bearer $script:accessToken" }
        TimeoutSec = 120
        ErrorAction = 'Stop'
    }
    if ($null -ne $Body) {
        $parameters.ContentType = 'application/json; charset=utf-8'
        $parameters.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 100 -Compress))
    }
    try {
        $response = Invoke-RestMethod @parameters
        if ($null -ne $response) {
            $response | ConvertTo-Json -Depth 100 -Compress | ConvertFrom-Json -AsHashtable
        }
    }
    catch {
        # API responses can contain SAS URLs; do not print the response or request.
        throw "Microsoft Store API $Method request failed. Check Partner Center and the API credentials; no sensitive response has been logged."
    }
}

function Get-UploadVersion {
    param([string]$Path)
    $upload = [IO.Compression.ZipFile]::OpenRead($Path)
    $bundlePath = Join-Path ([IO.Path]::GetTempPath()) "$([guid]::NewGuid()).msixbundle"
    try {
        $bundles = @($upload.Entries | Where-Object { $_.FullName.EndsWith('.msixbundle') })
        if ($bundles.Count -ne 1) { throw 'Expected exactly one MSIX bundle in the upload.' }
        $inputStream = $bundles[0].Open()
        $outputStream = [IO.File]::Create($bundlePath)
        try { $inputStream.CopyTo($outputStream) }
        finally { $inputStream.Dispose(); $outputStream.Dispose() }
        $bundle = [IO.Compression.ZipFile]::OpenRead($bundlePath)
        try {
            $entry = $bundle.GetEntry('AppxMetadata/AppxBundleManifest.xml')
            if ($null -eq $entry) { throw 'The bundle manifest is missing.' }
            $reader = [IO.StreamReader]::new($entry.Open())
            try { [xml]$manifest = $reader.ReadToEnd() }
            finally { $reader.Dispose() }
            return [version]$manifest.Bundle.Identity.Version
        }
        finally { $bundle.Dispose() }
    }
    finally {
        $upload.Dispose()
        if (Test-Path -LiteralPath $bundlePath) { Remove-Item -LiteralPath $bundlePath }
    }
}

$required = @('STORE_TENANT_ID', 'STORE_CLIENT_ID', 'STORE_CLIENT_SECRET', 'STORE_APP_ID')
$missing = @($required | Where-Object { [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_)) })
if ($missing.Count -gt 0) { throw "Missing repository secrets: $($missing -join ', ')." }
if ($env:STORE_APP_ID -notmatch '^[A-Za-z0-9]+$') { throw 'STORE_APP_ID must be the Store product ID.' }
if ($env:STORE_TENANT_ID -notmatch '^[A-Za-z0-9.-]+$') { throw 'STORE_TENANT_ID is not a valid tenant identifier.' }

$package = Get-Item -LiteralPath $PackagePath
if ($package.Extension -ne '.msixupload') { throw 'Expected a verified .msixupload package.' }
$packageVersion = Get-UploadVersion $package.FullName
$notes = Get-Content -LiteralPath $ReleaseNotesPath -Raw | ConvertFrom-Json -AsHashtable
if ($notes -isnot [System.Collections.IDictionary] -or $notes.Count -eq 0) {
    throw 'Release notes must be a non-empty JSON dictionary of listing language codes to plain text.'
}
foreach ($language in $notes.Keys) {
    if ($notes[$language] -isnot [string] -or [string]::IsNullOrWhiteSpace($notes[$language]) -or
        $notes[$language].Length -gt 1500) {
        throw "Release notes for $language must contain 1-1500 characters."
    }
}

$script:api = 'https://manage.devcenter.microsoft.com/v1.0/my'
try {
    $token = Invoke-RestMethod -Method Post `
        -Uri "https://login.microsoftonline.com/$($env:STORE_TENANT_ID)/oauth2/token" `
        -ContentType 'application/x-www-form-urlencoded' -TimeoutSec 120 -ErrorAction Stop -Body @{
            grant_type = 'client_credentials'
            client_id = $env:STORE_CLIENT_ID
            client_secret = $env:STORE_CLIENT_SECRET
            resource = 'https://manage.devcenter.microsoft.com'
        }
}
catch {
    throw 'Microsoft Store authentication failed. Verify the Partner Center application association and repository secrets.'
}
$script:accessToken = $token.access_token
if ([string]::IsNullOrWhiteSpace($script:accessToken)) { throw 'Authentication returned no access token.' }
if ($env:GITHUB_ACTIONS -eq 'true') { Write-Host "::add-mask::$script:accessToken" }

$appPath = "applications/$($env:STORE_APP_ID)"
$app = Invoke-StoreRequest Get $appPath
if ($null -ne $app.pendingApplicationSubmission) {
    throw "A Store submission is already pending. Finish or resolve it in Partner Center before rerunning. Nothing has been replaced or deleted."
}
if ($null -eq $app.lastPublishedApplicationSubmission) {
    throw 'Publish the initial submission, including age ratings, in Partner Center before enabling API updates.'
}

$published = Invoke-StoreRequest Get "$appPath/submissions/$($app.lastPublishedApplicationSubmission.id)"
foreach ($oldPackage in $published.applicationPackages) {
    if ([version]$oldPackage.version -ge $packageVersion) {
        throw "Package version $packageVersion must be newer than every published package. Do not resubmit an older build."
    }
}
foreach ($language in $notes.Keys) {
    if (!$published.listings.Contains($language)) {
        throw "Release notes language $language is not in the published listing. Add the listing in Partner Center first."
    }
}

$submissionId = $null
$archivePath = Join-Path ([IO.Path]::GetTempPath()) "$([guid]::NewGuid()).zip"
try {
    $zip = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $package.FullName, $package.Name) | Out-Null
    }
    finally { $zip.Dispose() }

    $submission = Invoke-StoreRequest Post "$appPath/submissions"
    $submissionId = $submission.id
    if ([string]::IsNullOrWhiteSpace($submissionId)) { throw 'Create submission returned no submission ID.' }
    Write-Host "Created Store submission $submissionId for package $packageVersion."
    if ($env:GITHUB_STEP_SUMMARY) {
        "## Microsoft Store submission`n`nSubmission: $submissionId`n`nPackage: $packageVersion" >> $env:GITHUB_STEP_SUMMARY
    }
    if ($env:GITHUB_ACTIONS -eq 'true') { Write-Host "::add-mask::$($submission.fileUploadUrl)" }
    try { $uploadUri = [uri]$submission.fileUploadUrl }
    catch { throw 'The submission returned an invalid package upload endpoint.' }
    if ($uploadUri.Scheme -ne 'https' -or !$uploadUri.Host.EndsWith('.blob.core.windows.net')) {
        throw 'The submission returned an unexpected package upload endpoint.'
    }
    foreach ($language in $notes.Keys) {
        if (!$submission.listings.Contains($language)) { throw "The new submission has no $language listing." }
        $submission.listings[$language].baseListing.releaseNotes = $notes[$language].Trim()
        if ($submission.listings[$language].platformOverrides -is [System.Collections.IDictionary]) {
            foreach ($override in $submission.listings[$language].platformOverrides.Values) {
                if ($override.Contains('releaseNotes')) { $override.releaseNotes = $notes[$language].Trim() }
            }
        }
    }
    foreach ($oldPackage in $submission.applicationPackages) { $oldPackage.fileStatus = 'PendingDelete' }
    $submission.applicationPackages = @($submission.applicationPackages) + @(@{
        fileName = $package.Name
        fileStatus = 'PendingUpload'
        minimumDirectXVersion = 'None'
        minimumSystemRam = 'None'
    })
    $submission.targetPublishMode = 'Immediate'
    # Keep all inherited listing, availability, certification, and pricing settings.
    foreach ($field in @('id', 'status', 'statusDetails', 'fileUploadUrl')) { $submission.Remove($field) }
    Invoke-StoreRequest Put "$appPath/submissions/$submissionId" $submission | Out-Null
    try {
        Invoke-WebRequest -Method Put -Uri $uploadUri -InFile $archivePath `
            -ContentType 'application/zip' -Headers @{
                'x-ms-blob-type' = 'BlockBlob'
                'x-ms-version' = '2021-12-02'
            } -TimeoutSec 1800 -ErrorAction Stop | Out-Null
    }
    catch {
        throw 'Store package upload failed. The draft has been retained; no SAS URL or response has been logged.'
    }
    Invoke-StoreRequest Post "$appPath/submissions/$submissionId/commit" | Out-Null
    for ($attempt = 0; $attempt -lt $PollAttempts; $attempt++) {
        $status = Invoke-StoreRequest Get "$appPath/submissions/$submissionId/status"
        Write-Host "Store submission $submissionId status: $($status.status)"
        if ($status.status -in @('PreProcessing', 'Certification', 'PendingPublication', 'Publishing', 'Published', 'Release')) {
            if ($env:GITHUB_STEP_SUMMARY) {
                "`nCommit accepted: $($status.status). Microsoft certification is still required; publication is automatic after approval." >> $env:GITHUB_STEP_SUMMARY
            }
            return
        }
        if ($status.status -notin @('CommitStarted', 'PendingCommit')) {
            foreach ($detail in $status.statusDetails.errors) { Write-Host "Store error code: $($detail.code). See Partner Center for details." }
            throw "Store submission $submissionId failed with status $($status.status)."
        }
        Start-Sleep -Seconds $PollSeconds
    }
    throw "Timed out waiting for Store submission $submissionId commit acceptance. Check Partner Center before rerunning."
}
catch {
    if ($null -ne $submissionId) {
        Write-Warning "Store submission $submissionId has been retained for investigation. Do not edit an API-created draft in Partner Center and then attempt to commit it through the API."
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath }
}
