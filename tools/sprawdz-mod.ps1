<#
.SYNOPSIS
    Sprawdza mod (C#) BEZ wchodzenia do gry: składnia i typy przez Roslyn + zestawy z Bin64.

.DESCRIPTION
    Błąd składni w modzie kosztuje inaczej pełne przeładowanie świata i szukanie linii
    w logu SE. Ten skrypt kompiluje mod do tymczasowej DLL w kilka sekund.

    CZEGO NIE SPRAWDZA: whitelisty ModAPI. Kompilator zna typy z Bin64 w całości, a gra
    dopuszcza tylko ich podzbiór — DateTimeOffset czy MyVisualScriptLogicProvider
    przechodzą tutaj, a w grze wywalają mod. Whitelistę weryfikuje dopiero uruchomienie.

    Pułapki (wynik sesji 2026-08-01, patrz CLAUDE.md):
      * csc.exe z Microsoft.NET\Framework64 umie tylko C# 5 i wywraca się na MESApi.cs —
        potrzebny Roslyn z MSBuild\Current\Bin\Roslyn;
      * natywnych DLL z Bin64 (VRage.Native, Havok, steam_api64) nie wolno podawać
        jako -r: (CS0009), stąd filtr niżej;
      * bez netstandard.dll z Facades sypie się CS0012 na ValueType.

.PARAMETER Gra
    Katalog Bin64 Space Engineers. Domyślnie szuka w typowych lokalizacjach Steam.

.PARAMETER Roslyn
    Katalog z csc.exe (Roslyn). Domyślnie szuka w Visual Studio przez vswhere.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\sprawdz-mod.ps1

.EXAMPLE
    powershell -File tools\sprawdz-mod.ps1 -Gra "D:\Steam\steamapps\common\SpaceEngineers\Bin64"
#>
[CmdletBinding()]
param(
    [string]$Gra,
    [string]$Roslyn
)

$ErrorActionPreference = "Stop"

function Znajdz-Bin64 {
    $kandydaci = @(
        "${env:ProgramFiles(x86)}\Steam\steamapps\common\SpaceEngineers\Bin64",
        "${env:ProgramFiles}\Steam\steamapps\common\SpaceEngineers\Bin64"
    )
    # Steam trzyma dodatkowe biblioteki w libraryfolders.vdf — przeszukujemy je też,
    # bo gra rzadko stoi na dysku systemowym. Sam plik potrafi leżeć w kilku miejscach
    # (instalacja 32- i 64-bitowa, przeniesiony klient), więc sprawdzamy wszystkie.
    $vdfy = @(
        "${env:ProgramFiles(x86)}\Steam\steamapps\libraryfolders.vdf",
        "${env:ProgramFiles}\Steam\steamapps\libraryfolders.vdf",
        "$env:LOCALAPPDATA\..\..\Steam\steamapps\libraryfolders.vdf"
    )
    foreach ($vdf in $vdfy) {
        if (-not (Test-Path $vdf)) { continue }
        foreach ($linia in Get-Content $vdf) {
            if ($linia -match '"path"\s+"(.+?)"') {
                $sciezka = $Matches[1] -replace '\\\\', '\'
                $kandydaci += Join-Path $sciezka "steamapps\common\SpaceEngineers\Bin64"
            }
        }
    }
    # Ostatnia deska ratunku: biblioteka Steam bez własnego libraryfolders.vdf (tak stoi
    # gra na maszynie deweloperskiej — D:\SteamLibrary) nie trafiłaby tu nigdy, a skrypt
    # kończył się wtedy „Nie znalazłem Bin64" mimo zainstalowanej gry (2026-08-04).
    foreach ($dysk in (Get-PSDrive -PSProvider FileSystem)) {
        foreach ($katalog in @("SteamLibrary", "Steam", "Games\SteamLibrary")) {
            $kandydaci += Join-Path $dysk.Root "$katalog\steamapps\common\SpaceEngineers\Bin64"
        }
    }
    foreach ($k in $kandydaci) {
        if (Test-Path (Join-Path $k "Sandbox.Common.dll")) { return $k }
    }
    return $null
}

function Znajdz-Roslyn {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $vs = & $vswhere -latest -products * -property installationPath
        if ($vs) {
            $sciezka = Join-Path $vs "MSBuild\Current\Bin\Roslyn"
            if (Test-Path (Join-Path $sciezka "csc.exe")) { return $sciezka }
        }
    }
    $globalne = Get-ChildItem "${env:ProgramFiles}\Microsoft Visual Studio", `
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio" -Recurse -Filter "csc.exe" `
        -ErrorAction SilentlyContinue | Where-Object { $_.FullName -like "*Roslyn*" } |
        Select-Object -First 1
    if ($globalne) { return $globalne.DirectoryName }
    return $null
}

if (-not $Gra) { $Gra = Znajdz-Bin64 }
if (-not $Gra) {
    Write-Host "Nie znalazłem Bin64 Space Engineers. Podaj ręcznie: -Gra <ścieżka>" -ForegroundColor Red
    exit 2
}
if (-not $Roslyn) { $Roslyn = Znajdz-Roslyn }
if (-not $Roslyn) {
    Write-Host "Nie znalazłem Roslyna (csc.exe). Zainstaluj Visual Studio albo podaj -Roslyn <ścieżka>." -ForegroundColor Red
    Write-Host "UWAGA: csc.exe z Microsoft.NET\Framework64 NIE wystarczy (umie tylko C# 5)." -ForegroundColor Yellow
    exit 2
}

$repo = Split-Path -Parent $PSScriptRoot
$zrodla = Join-Path $repo "mod\Data\Scripts\ZyweFrakcje\*.cs"
$pliki = @(Get-ChildItem $zrodla)
if ($pliki.Count -eq 0) {
    Write-Host "Nie znalazłem plików moda w $zrodla" -ForegroundColor Red
    exit 2
}

# Zestawy referencyjne. Natywne DLL odpadają (CS0009) — bierzemy tylko te, które
# faktycznie są zestawami .NET, i dokładamy netstandard z Facades (CS0012 na ValueType).
$wzorce = @("Sandbox*.dll", "VRage*.dll", "SpaceEngineers*.dll", "protobuf*.dll")
$natywne = @("VRage.Native.dll", "Havok.dll", "steam_api64.dll", "SteamSDK.Native.dll")
$referencje = @()
foreach ($wzorzec in $wzorce) {
    foreach ($dll in Get-ChildItem (Join-Path $Gra $wzorzec) -ErrorAction SilentlyContinue) {
        if ($natywne -notcontains $dll.Name) { $referencje += $dll.FullName }
    }
}
$netstandardKandydaci = @(
    (Join-Path $Gra "Facades\netstandard.dll"),
    (Join-Path $Gra "netstandard.dll")
)
$netstandard = $netstandardKandydaci | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($netstandard) {
    $referencje += $netstandard
} else {
    Write-Host "UWAGA: brak netstandard.dll (szukano: $($netstandardKandydaci -join ', ')) — spodziewaj się CS0012 na ValueType." -ForegroundColor Yellow
}

$wyjscie = Join-Path ([System.IO.Path]::GetTempPath()) ("zf_mod_" + [Guid]::NewGuid().ToString("N") + ".dll")
$argumenty = @(
    "-nologo",
    "-langversion:6",          # ModAPI: mod kompiluje się w grze jako C# 6
    "-target:library",
    "-out:$wyjscie"
) + ($referencje | ForEach-Object { "-r:$_" }) + ($pliki | ForEach-Object { $_.FullName })

Write-Host "Bin64:  $Gra"
Write-Host "Roslyn: $Roslyn"
Write-Host "Plików moda: $($pliki.Count), referencji: $($referencje.Count)"
Write-Host ""

$csc = Join-Path $Roslyn "csc.exe"
& $csc $argumenty
$kod = $LASTEXITCODE

if (Test-Path $wyjscie) { Remove-Item $wyjscie -Force -ErrorAction SilentlyContinue }

Write-Host ""
if ($kod -eq 0) {
    Write-Host "Mod kompiluje się (składnia i typy OK)." -ForegroundColor Green
    Write-Host "Pamiętaj: to NIE sprawdza whitelisty ModAPI — tę weryfikuje dopiero gra." -ForegroundColor Yellow
} else {
    Write-Host "Kompilacja NIE przeszła (kod $kod) — popraw błędy powyżej." -ForegroundColor Red
}
exit $kod
