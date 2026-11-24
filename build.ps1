param([switch]$Release, [switch]$Sign, [string]$CertThumbprint = '', [string]$TimestampUrl = 'http://timestamp.digicert.com')
# Builds build\LordOfTheRingsGodModeCheat.exe (the "Lord of the Rings God Mode Cheat", formerly "Witch-King Trainer") using only the C# compiler that ships with Windows (.NET Framework 4.x).
# No SDK, NuGet package or other download is required.
#   build.ps1                     compile the trainer and the installer into build\
#   build.ps1 -Release            ... and put installer, portable ZIP, documents and SHA-256 list into dist\
#   build.ps1 -Release -Sign -CertThumbprint <thumbprint of your code-signing certificate>
#                                 ... and sign the trainer and the installer (needs signtool.exe from the Windows SDK)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
$obj = Join-Path $root 'build\obj'
New-Item -ItemType Directory -Force $obj | Out-Null

function Sign-File([string]$file) {
    # Signs with YOUR certificate (already in your certificate store). Nothing is signed unless -Sign is given.
    if (-not $CertThumbprint) { throw 'Signing needs -CertThumbprint <thumbprint> (see src\Setup\SECURITY.txt, "Code signing").' }
    $tool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe' -ErrorAction SilentlyContinue | Sort-Object FullName | Select-Object -Last 1
    if (-not $tool) { throw 'signtool.exe was not found. It is part of the free Windows SDK ("Signing Tools for Desktop Apps"); install it yourself, nothing is downloaded by this script.' }
    & $tool.FullName sign /sha1 $CertThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 /d 'Lord of the Rings God Mode Cheat' $file
    if ($LASTEXITCODE -ne 0) { throw "signing failed: $file" }
}

# 1) icon: made from the artwork in src\App\icon-source.png (falls back to a drawn ring if that file is missing)
& $csc /nologo /optimize+ /out:"$obj\MakeIcon.exe" /r:System.Drawing.dll "$root\dev\tools\MakeIcon.cs"
if ($LASTEXITCODE -ne 0) { throw 'icon tool build failed' }
& "$obj\MakeIcon.exe" "$obj\app.ico" "$obj\app.png" "$root\src\App\icon-source.png" | Out-Null

# 2) the trainer (32-bit, like the game). The faction lists (generated from the games' own data by dev\tools\CatalogGen) travel inside it.
$src = @(Get-ChildItem "$root\src\Core\*.cs", "$root\src\App\*.cs", "$root\src\Shared\*.cs" | ForEach-Object { $_.FullName })
$refs = @("$fw\WPF\PresentationFramework.dll", "$fw\WPF\PresentationCore.dll", "$fw\WPF\WindowsBase.dll", "$fw\System.Xaml.dll", 'System.Windows.Forms.dll', 'System.Drawing.dll') | ForEach-Object { "/r:$_" }
$res = @("/resource:$root\src\App\MainWindow.xaml,WitchKingTrainer.MainWindow.xaml", "/resource:$obj\app.png,WitchKingTrainer.app.png", "/resource:$obj\app.ico,WitchKingTrainer.app.ico")
foreach ($cat in 'catalog-rotwk.txt', 'catalog-bfme2.txt') { if (Test-Path "$root\src\App\$cat") { $res += "/resource:$root\src\App\$cat,WitchKingTrainer.$cat" } else { Write-Warning "$cat is missing - the All Factions lists for that game will be empty" } }
$out = Join-Path $root 'build\LordOfTheRingsGodModeCheat.exe'
& $csc /nologo /optimize+ /target:winexe /platform:x86 /out:"$out" /win32icon:"$obj\app.ico" /win32manifest:"$root\src\App\app.manifest" $res $refs $src
if ($LASTEXITCODE -ne 0) { throw 'trainer build failed' }
if ($Sign) { Sign-File $out }
"Built $out ($([int]((Get-Item $out).Length / 1KB)) KB)"

# 3) the installer: a small program that carries the trainer and its documents inside itself
$docs = 'README.txt', 'SECURITY.txt', 'TESTING-GUIDE.txt'
foreach ($d in $docs) { if (-not (Test-Path "$root\src\Setup\$d")) { throw "missing document src\Setup\$d" } }
$payload = @("/resource:$out,Payload.App.exe") + ($docs | ForEach-Object { "/resource:$root\src\Setup\$_,Payload.$_" })
$setup = Join-Path $root 'build\LordOfTheRingsGodModeCheat-Setup.exe'
& $csc /nologo /optimize+ /target:winexe /platform:x86 /out:"$setup" /win32icon:"$obj\app.ico" /win32manifest:"$root\src\App\app.manifest" $payload `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll "$root\src\Setup\Setup.cs" "$root\src\Shared\InstallInfo.cs"
if ($LASTEXITCODE -ne 0) { throw 'setup build failed' }
if ($Sign) { Sign-File $setup }
"Built $setup ($([int]((Get-Item $setup).Length / 1KB)) KB)"

# 4) release: installer, portable copy (no installer: unzip anywhere and run), documents, checksums
if ($Release) {
    $dist = Join-Path $root 'dist'; New-Item -ItemType Directory -Force $dist | Out-Null
    $ver = '1.0'   # the version stays 1.0; builds are told apart by date and by SHA256SUMS.txt
    Copy-Item $setup "$dist\LordOfTheRingsGodModeCheat-Setup.exe" -Force
    foreach ($d in $docs) { Copy-Item "$root\src\Setup\$d" "$dist\$d" -Force }
    $pdir = Join-Path $obj 'portable\LordOfTheRingsGodModeCheat-Portable'
    if (Test-Path (Join-Path $obj 'portable')) { Remove-Item (Join-Path $obj 'portable') -Recurse -Force }
    New-Item -ItemType Directory -Force $pdir | Out-Null
    Copy-Item $out $pdir; foreach ($d in $docs) { Copy-Item "$root\src\Setup\$d" $pdir }
    Set-Content -Path "$pdir\portable.txt" -Encoding ASCII -Value @(
        'This file makes the trainer "portable": while it lies next to LordOfTheRingsGodModeCheat.exe, the trainer keeps its',
        'settings and its session log in the TrainerData folder beside the program instead of your Windows profile.',
        'Nothing is installed. To remove the trainer, delete this folder.')
    $zip = "$dist\LordOfTheRingsGodModeCheat-$ver-Portable.zip"; if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path $pdir -DestinationPath $zip -CompressionLevel Optimal
    $sums = Get-ChildItem $dist -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object Name | ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name }
    $sums += '{0}  {1}' -f (Get-FileHash $out -Algorithm SHA256).Hash.ToLower(), 'LordOfTheRingsGodModeCheat.exe (the program inside the installer and the portable ZIP)'
    Set-Content -Path "$dist\SHA256SUMS.txt" -Encoding ASCII -Value $sums
    "Released to dist\ (installer, portable ZIP $ver, documents, SHA256SUMS.txt)"
}
