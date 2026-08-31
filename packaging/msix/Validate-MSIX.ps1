[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [string]$CertificatePath,

    [switch]$RunWack,

    [switch]$KeepInstalled,

    [string]$ReportOutputPath
)

$ErrorActionPreference = "Stop"

# Works in both Windows PowerShell 5.1 and PowerShell 7; $IsWindows only exists in PowerShell Core.
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw "Validate-MSIX.ps1 must be run on Windows."
}

$packagePath = (Resolve-Path -LiteralPath $PackagePath).Path
$certificatePath = $null
if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
    $certificatePath = (Resolve-Path -LiteralPath $CertificatePath).Path
}

$sdkBin = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
$makeAppx = Get-ChildItem -LiteralPath $sdkBin -Filter MakeAppx.exe -File -Recurse |
    Where-Object { $_.FullName -match '\\x64\\MakeAppx\.exe$' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if ($null -eq $makeAppx) {
    throw "MakeAppx.exe was not found. Install a current Windows SDK."
}

$unpackDirectory = Join-Path $env:TEMP ("PaperTodo-msix-validate-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $unpackDirectory -Force | Out-Null

$trustedThumbprint = $null
$trustedBefore = $false
$installedPackage = $null

try {
    & $makeAppx.FullName unpack /p $packagePath /d $unpackDirectory /o | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "MakeAppx failed to unpack the package (exit $LASTEXITCODE)."
    }

    $manifestPath = Join-Path $unpackDirectory "AppxManifest.xml"
    [xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
    $ns = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $ns.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $identity = $manifest.SelectSingleNode('/f:Package/f:Identity', $ns)
    $application = $manifest.SelectSingleNode('/f:Package/f:Applications/f:Application', $ns)
    if ($null -eq $identity -or $null -eq $application) {
        throw "AppxManifest.xml is missing Identity or Application."
    }

    $identityName = $identity.GetAttribute('Name')
    $applicationId = $application.GetAttribute('Id')
    Write-Host "Package identity: $identityName"
    Write-Host "Application id:   $applicationId"

    if ($certificatePath) {
        $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
        $trustedThumbprint = $certificate.Thumbprint
        $trustedPath = "Cert:\CurrentUser\TrustedPeople\$trustedThumbprint"
        $trustedBefore = Test-Path -LiteralPath $trustedPath
        if (-not $trustedBefore) {
            Import-Certificate -FilePath $certificatePath -CertStoreLocation "Cert:\CurrentUser\TrustedPeople" | Out-Null
        }
    }

    foreach ($existing in @(Get-AppxPackage -Name $identityName -ErrorAction SilentlyContinue)) {
        Remove-AppxPackage -Package $existing.PackageFullName -ErrorAction Stop
    }

    try {
        Add-AppxPackage -Path $packagePath -ErrorAction Stop
    }
    catch {
        if (-not $certificatePath) {
            throw "Package installation failed. For the CI TEST package, pass -CertificatePath PaperTodo-MSIX-test.cer. The unsigned STORE package is intended for Partner Center, not direct installation. Original error: $($_.Exception.Message)"
        }
        throw
    }

    $installedPackages = @(Get-AppxPackage -Name $identityName -ErrorAction SilentlyContinue)
    if ($installedPackages.Count -ne 1) {
        throw "Expected exactly one installed package named '$identityName', found $($installedPackages.Count)."
    }
    $installedPackage = $installedPackages[0]
    Write-Host "Installed: $($installedPackage.PackageFullName)"

    $appUserModelId = "$($installedPackage.PackageFamilyName)!$applicationId"
    Start-Process explorer.exe -ArgumentList "shell:AppsFolder\$appUserModelId"
    Start-Sleep -Seconds 5

    $paperTodoProcesses = @(Get-Process -Name PaperTodo -ErrorAction SilentlyContinue)
    if ($paperTodoProcesses.Count -eq 0) {
        throw "PaperTodo did not remain running after packaged activation."
    }
    Write-Host "Packaged launch smoke test passed (PaperTodo process is running)."

    foreach ($process in $paperTodoProcesses) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }

    if ($RunWack) {
        $identityObject = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = [Security.Principal.WindowsPrincipal]::new($identityObject)
        $isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        if (-not $isAdmin) {
            throw "Windows App Certification Kit must be run from an elevated shell. Re-run PowerShell as administrator."
        }

        $appCert = Join-Path "${env:ProgramFiles(x86)}" "Windows Kits\10\App Certification Kit\appcert.exe"
        if (-not (Test-Path -LiteralPath $appCert -PathType Leaf)) {
            throw "appcert.exe was not found. Install the Windows App Certification Kit from the Windows SDK."
        }

        if ([string]::IsNullOrWhiteSpace($ReportOutputPath)) {
            $ReportOutputPath = Join-Path (Split-Path -Parent $packagePath) "PaperTodo-WACK-report.xml"
        }
        else {
            $ReportOutputPath = [IO.Path]::GetFullPath($ReportOutputPath)
        }

        & $appCert reset
        if ($LASTEXITCODE -ne 0) {
            throw "appcert.exe reset failed with exit code $LASTEXITCODE."
        }

        & $appCert test `
            -packagefullname $installedPackage.PackageFullName `
            -reportoutputpath $ReportOutputPath
        if ($LASTEXITCODE -ne 0) {
            throw "Windows App Certification Kit failed with exit code $LASTEXITCODE. Report: $ReportOutputPath"
        }

        Write-Host "WACK completed successfully: $ReportOutputPath"
    }
}
finally {
    Get-Process -Name PaperTodo -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue

    if (-not $KeepInstalled -and $null -ne $installedPackage) {
        Remove-AppxPackage -Package $installedPackage.PackageFullName -ErrorAction SilentlyContinue
    }

    if ($trustedThumbprint -and -not $trustedBefore) {
        Remove-Item -LiteralPath "Cert:\CurrentUser\TrustedPeople\$trustedThumbprint" -Force -ErrorAction SilentlyContinue
    }

    if (Test-Path -LiteralPath $unpackDirectory) {
        Remove-Item -LiteralPath $unpackDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
