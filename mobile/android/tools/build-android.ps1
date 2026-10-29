param([ValidateSet('Debug', 'Release', 'Test')][string]$Mode = 'Release')
$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path -Parent $PSScriptRoot
$versionMatch = [regex]::Match((Get-Content -LiteralPath (Join-Path $projectDirectory 'app\build.gradle.kts') -Raw), 'versionName\s*=\s*"([0-9A-Za-z.+-]+)"')
if (-not $versionMatch.Success) { throw '未找到 Android versionName。' }
$artifactName = 'PaperTodo-Mobile-v' + $versionMatch.Groups[1].Value
Push-Location -LiteralPath $projectDirectory
try {
    if (-not $env:JAVA_HOME -or -not (Test-Path -LiteralPath (Join-Path $env:JAVA_HOME 'bin\java.exe'))) {
        throw '请将 JAVA_HOME 指向 JDK 17 或更高版本，并配置 ANDROID_HOME。'
    }
    if ($Mode -eq 'Release') {
        $signingDirectory = Join-Path $projectDirectory '.local'
        New-Item -ItemType Directory -Path $signingDirectory -Force | Out-Null
        $signingProperties = Join-Path $signingDirectory 'signing.properties'
        $keyStorePath = Join-Path $signingDirectory 'papertodo-release.keystore'
        if (-not (Test-Path -LiteralPath $signingProperties)) {
            if (Test-Path -LiteralPath $keyStorePath) { throw '发现现有签名文件但缺少 signing.properties，请恢复原密码配置，不要覆盖原签名。' }
            $passwordBytes = New-Object byte[] 32
            $random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
            try { $random.GetBytes($passwordBytes) } finally { $random.Dispose() }
            $password = [BitConverter]::ToString($passwordBytes).Replace('-', '').ToLowerInvariant()
            $env:PAPERTODO_KEYSTORE_PASSWORD = $password
            try {
                & (Join-Path $env:JAVA_HOME 'bin\keytool.exe') -genkeypair -keystore $keyStorePath -alias papertodo-mobile -keyalg RSA -keysize 3072 -validity 10000 -dname 'CN=PaperTodo Mobile Local Build' -storepass:env PAPERTODO_KEYSTORE_PASSWORD -keypass:env PAPERTODO_KEYSTORE_PASSWORD -noprompt
                if ($LASTEXITCODE -ne 0) { throw '生成签名失败。' }
                @("storeFile=.local/papertodo-release.keystore", "storePassword=$password", 'keyAlias=papertodo-mobile', "keyPassword=$password") | Set-Content -LiteralPath $signingProperties -Encoding Ascii
            } finally { Remove-Item Env:\PAPERTODO_KEYSTORE_PASSWORD -ErrorAction SilentlyContinue; $password = $null }
        }
        if (-not (Test-Path -LiteralPath $keyStorePath)) { throw '缺少原签名密钥，请恢复 .local 中的密钥备份，避免产生无法覆盖安装的 APK。' }
        & .\gradlew.bat :app:testDebugUnitTest :app:lintRelease :app:assembleRelease
        if ($LASTEXITCODE -ne 0) { throw '安卓构建或检查未通过。' }
        $outputDirectory = Join-Path $projectDirectory 'dist'
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
        $destination = Join-Path $outputDirectory "$artifactName.apk"
        Copy-Item -LiteralPath 'app\build\outputs\apk\release\app-release.apk' -Destination $destination
        $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $artifactName.apk" | Set-Content -LiteralPath (Join-Path $outputDirectory 'APK-SHA256.txt') -Encoding Ascii
        Write-Output "已生成：$destination"
        Write-Output '请私下备份 .local 的签名密钥与密码配置；它们已被 Git 忽略。'
    } elseif ($Mode -eq 'Debug') {
        & .\gradlew.bat :app:assembleDebug :app:assembleDebugAndroidTest
        if ($LASTEXITCODE -ne 0) { throw 'Debug 构建未通过。' }
    } else {
        & .\gradlew.bat :app:testDebugUnitTest :app:lintDebug
        if ($LASTEXITCODE -ne 0) { throw '测试或静态检查未通过。' }
    }
} finally { Pop-Location }
