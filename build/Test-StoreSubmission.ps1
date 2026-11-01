$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-StoreTest {
    param([bool]$Condition, [string]$Message)
    if (!$Condition) { throw $Message }
}

# All HTTP commands are intercepted; these tests cannot submit to the Store.
function Invoke-RestMethod {
    param($Method, $Uri, $Headers, $Body, $ContentType, $TimeoutSec, $ErrorAction)
    $state = $global:InkMotionStoreTestState
    $state.calls.Add("$Method $Uri")
    if ($Uri -like 'https://login.microsoftonline.com/*') {
        Assert-StoreTest ($Body.resource -eq 'https://manage.devcenter.microsoft.com') 'Incorrect token audience.'
        return @{ access_token = 'test-token' }
    }
    Assert-StoreTest ($Headers.Authorization -eq 'Bearer test-token') 'Missing Store authentication.'
    if ($Uri -match '/status$') {
        $state.polls++
        return @{
            status = if ($state.polls -eq 1) { 'CommitStarted' } else { $state.status }
            statusDetails = @{ errors = @(@{ code = 'TestFailure'; details = 'Do not log this response' }) }
        }
    }
    if ($Uri -match '/commit$') { return @{ } }
    if ($Method -eq 'Put') {
        $state.updated = [Text.Encoding]::UTF8.GetString($Body) | ConvertFrom-Json -AsHashtable
        return @{ }
    }
    if ($Method -eq 'Post') {
        if ($state.createFails) { throw 'Sensitive response: https://example.invalid/?sig=secret' }
        $state.draft.id = 'new-submission'
        $state.draft.fileUploadUrl = 'https://test.blob.core.windows.net/upload?sig=test'
        $state.draft.status = 'PendingCommit'
        $state.draft.statusDetails = @{}
        return $state.draft
    }
    if ($Uri -match '/submissions/published$') { return $state.draft }
    if ($Uri -match '/applications/TESTAPP$') {
        return @{
            pendingApplicationSubmission = if ($state.pending) { @{ id = 'other-draft' } } else { $null }
            lastPublishedApplicationSubmission = if ($state.initialMissing) { $null } else { @{ id = 'published' } }
        }
    }
    throw "Unexpected mocked HTTP request: $Method $Uri"
}

function Invoke-WebRequest {
    param($Method, $Uri, $InFile, $ContentType, $Headers, $TimeoutSec, $ErrorAction)
    $state = $global:InkMotionStoreTestState
    $state.uploads++
    Assert-StoreTest ($Method -eq 'Put' -and $Headers['x-ms-blob-type'] -eq 'BlockBlob') 'Incorrect blob upload.'
    $zip = [IO.Compression.ZipFile]::OpenRead($InFile)
    try {
        Assert-StoreTest ($zip.Entries.Count -eq 1 -and $zip.Entries[0].Name -eq 'test.msixupload') 'Upload ZIP must contain the MSIX upload with matching fileName.'
    }
    finally { $zip.Dispose() }
    if ($state.uploadFails) { throw 'Sensitive upload URL must not be logged.' }
    return @{ StatusCode = 201 }
}

function Start-Sleep { param($Seconds) }

$directory = Join-Path ([IO.Path]::GetTempPath()) "InkMotion-StoreTests-$([guid]::NewGuid())"
$savedEnvironment = @{}
$environmentNames = @('STORE_TENANT_ID', 'STORE_CLIENT_ID', 'STORE_CLIENT_SECRET', 'STORE_APP_ID', 'GITHUB_STEP_SUMMARY', 'GITHUB_ACTIONS')
foreach ($name in $environmentNames) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $null) }
    $env:STORE_TENANT_ID = 'test-tenant'
    $env:STORE_CLIENT_ID = 'test-client'
    $env:STORE_CLIENT_SECRET = 'not-a-real-credential'
    $env:STORE_APP_ID = 'TESTAPP'
    New-Item -ItemType Directory -Path $directory | Out-Null
    $manifestPath = Join-Path $directory 'AppxBundleManifest.xml'
    [IO.File]::WriteAllText($manifestPath, '<Bundle><Identity Version="1.0.201.0" /></Bundle>')
    $bundlePath = Join-Path $directory 'test.msixbundle'
    $zip = [IO.Compression.ZipFile]::Open($bundlePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $manifestPath, 'AppxMetadata/AppxBundleManifest.xml') | Out-Null
    }
    finally { $zip.Dispose() }
    $packagePath = Join-Path $directory 'test.msixupload'
    $zip = [IO.Compression.ZipFile]::Open($packagePath, [IO.Compression.ZipArchiveMode]::Create)
    try { [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $bundlePath, 'test.msixbundle') | Out-Null }
    finally { $zip.Dispose() }
    $notesPath = Join-Path $directory 'notes.json'
    $scriptPath = Join-Path $PSScriptRoot 'Submit-StoreUpdate.ps1'
    $cases = @(
        @{ name = 'happy' },
        @{ name = 'pending'; pending = $true; error = 'already pending' },
        @{ name = 'initial'; initialMissing = $true; error = 'initial submission' },
        @{ name = 'old-version'; version = '1.0.202.0'; error = 'must be newer' },
        @{ name = 'language'; language = 'fr-fr'; error = 'not in the published listing' },
        @{ name = 'empty-notes'; notes = ''; error = '1-1500 characters' },
        @{ name = 'long-notes'; notes = ('a' * 1501); error = '1-1500 characters' },
        @{ name = 'missing-secret'; missingSecret = $true; error = 'Missing repository secrets' },
        @{ name = 'create-failure'; createFails = $true; error = 'API Post request failed' },
        @{ name = 'upload-failure'; uploadFails = $true; error = 'package upload failed' },
        @{ name = 'commit-failure'; status = 'CommitFailed'; error = 'failed with status CommitFailed' },
        @{ name = 'unknown-status'; status = 'Unexpected'; error = 'failed with status Unexpected' },
        @{ name = 'timeout'; status = 'CommitStarted'; error = 'Timed out' }
    )
    foreach ($case in $cases) {
        $global:InkMotionStoreTestState = @{
            calls = [Collections.Generic.List[string]]::new()
            polls = 0; uploads = 0; updated = $null
            status = if ($case.ContainsKey('status')) { $case.status } else { 'PreProcessing' }
            pending = $case.ContainsKey('pending')
            initialMissing = $case.ContainsKey('initialMissing')
            createFails = $case.ContainsKey('createFails')
            uploadFails = $case.ContainsKey('uploadFails')
            draft = @{
                applicationPackages = @(@{
                    fileName = 'old.msixupload'
                    fileStatus = 'Uploaded'
                    version = if ($case.ContainsKey('version')) { $case.version } else { '1.0.200.0' }
                })
                listings = @{
                    'en-us' = @{
                        baseListing = @{ releaseNotes = 'Old notes'; description = 'Keep description'; images = @('existing-image') }
                        platformOverrides = @{ Windows81 = @{ releaseNotes = 'Old platform notes'; description = 'Keep override' } }
                    }
                    'de-de' = @{ baseListing = @{ releaseNotes = 'Keep German notes' } }
                }
                pricing = @{ priceId = 'Free' }
                visibility = 'Public'
                targetPublishMode = 'Manual'
            }
        }
        $state = $global:InkMotionStoreTestState
        $language = if ($case.ContainsKey('language')) { $case.language } else { 'en-us' }
        $text = if ($case.ContainsKey('notes')) { $case.notes } else { "Reviewed notes with Unicode: caf$([char]0xE9)." }
        @{ $language = $text } | ConvertTo-Json | Set-Content -LiteralPath $notesPath -Encoding utf8
        $env:STORE_CLIENT_SECRET = if ($case.ContainsKey('missingSecret')) { '' } else { 'not-a-real-credential' }
        $caught = $null
        try { & $scriptPath -PackagePath $packagePath -ReleaseNotesPath $notesPath -PollAttempts 3 -PollSeconds 0 }
        catch { $caught = $_.Exception.Message }
        if ($case.ContainsKey('error')) {
            Assert-StoreTest ($null -ne $caught -and $caught.Contains($case.error)) "Case $($case.name): unexpected error '$caught'."
            Assert-StoreTest (!$caught.Contains('sig=secret')) 'API failure exposed a sensitive response.'
        }
        else {
            Assert-StoreTest ($null -eq $caught) "Happy path failed: $caught"
            Assert-StoreTest ($state.updated.listings['en-us'].baseListing.releaseNotes -eq $text) 'Reviewed UTF-8 notes were not applied.'
            Assert-StoreTest ($state.updated.listings['en-us'].baseListing.description -eq 'Keep description') 'Description changed.'
            Assert-StoreTest ($state.updated.listings['en-us'].platformOverrides.Windows81.releaseNotes -eq $text) 'Platform override retained stale notes.'
            Assert-StoreTest ($state.updated.listings['de-de'].baseListing.releaseNotes -eq 'Keep German notes') 'Unspecified locale changed.'
            Assert-StoreTest ($state.updated.pricing.priceId -eq 'Free' -and $state.updated.visibility -eq 'Public') 'Availability or pricing changed.'
            Assert-StoreTest ($state.updated.applicationPackages[0].fileStatus -eq 'PendingDelete') 'Old package was not retired.'
            Assert-StoreTest ($state.updated.applicationPackages[1].fileName -eq 'test.msixupload') 'New package missing.'
            Assert-StoreTest ($state.updated.targetPublishMode -eq 'Immediate') 'Automatic publication was not requested.'
            Assert-StoreTest (!$state.updated.Contains('fileUploadUrl') -and !$state.updated.Contains('status')) 'Response-only metadata sent back.'
            Assert-StoreTest ($state.uploads -eq 1 -and $state.polls -eq 2) 'Upload or status polling missing.'
        }
        if ($case.name -in @('pending', 'initial', 'old-version', 'language', 'empty-notes', 'long-notes', 'missing-secret')) {
            Assert-StoreTest (@($state.calls | Where-Object { $_ -like 'Post *' -and $_ -notlike '*login.microsoftonline.com*' }).Count -eq 0) 'Validation failure mutated Store data.'
        }
        Assert-StoreTest (@($state.calls | Where-Object { $_ -like 'Delete *' }).Count -eq 0) 'Automation must never delete a pending submission.'
        if ($case.name -eq 'upload-failure') {
            Assert-StoreTest (@($state.calls | Where-Object { $_ -like '*/commit' }).Count -eq 0) 'Failed upload was committed.'
        }
        Write-Host "PASS: $($case.name)"
    }
    $reviewedNotes = Get-Content (Join-Path $PSScriptRoot 'store-release-notes.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert-StoreTest ($reviewedNotes['en-us'].Length -in 1..1500) 'Checked-in English notes are invalid.'
}
finally {
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name]) }
    Remove-Variable InkMotionStoreTestState -Scope Global -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse }
}
