# Antfarm launcher.
#
# Starts a local dedicated server, opens the live view panel, and joins you to
# the world. The panel is the point: it is the thing you can leave open and
# watch while the colony works, with Terraria closed or open.
#
# Nothing here needs configuring by hand. It finds tModLoader through Steam.

$ErrorActionPreference = 'Stop'

$GamePort  = 7777
$PanelPort = 7778
$WorldName = 'Antfarm'

function Say($text, $colour = 'Gray') { Write-Host "  $text" -ForegroundColor $colour }

Write-Host ''
Write-Host '  ANTFARM' -ForegroundColor White
Write-Host '  ten tribes, one world, no pause button' -ForegroundColor DarkGray
Write-Host ''

# --- find tModLoader -------------------------------------------------

function Find-TModLoader {
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if (-not $steam) { return $null }

    $roots = @($steam)

    # Steam keeps extra install drives in libraryfolders.vdf.
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        foreach ($line in Get-Content $vdf) {
            if ($line -match '"path"\s+"(.+?)"') {
                $roots += $matches[1].Replace('\\', '\')
            }
        }
    }

    foreach ($root in $roots) {
        $candidate = Join-Path $root 'steamapps\common\tModLoader'
        if (Test-Path (Join-Path $candidate 'tModLoader.dll')) { return $candidate }
    }

    return $null
}

$tml = Find-TModLoader
if (-not $tml) {
    Say 'Could not find tModLoader through Steam.' Red
    Say 'Install it from Steam (it is free if you own Terraria), then run this again.' Red
    exit 1
}
Say "tModLoader: $tml" DarkGray

# --- find the save directory ----------------------------------------

$saveDir = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'My Games\Terraria\tModLoader'
if (-not (Test-Path $saveDir)) {
    Say 'No tModLoader save folder yet.' Yellow
    Say 'Launch tModLoader once so it creates one, then run this again.' Yellow
    exit 1
}

$modFile = Join-Path $saveDir 'Mods\Antfarm.tmod'
if (-not (Test-Path $modFile)) {
    Say 'Antfarm is not built yet.' Yellow
    Say 'Put src\Antfarm into your ModSources folder and build it in' Yellow
    Say 'tModLoader under Workshop -> Develop Mods, then run this again.' Yellow
    Say "ModSources: $saveDir\ModSources" DarkGray
    exit 1
}

# --- make sure the mod is switched on -------------------------------

$enabledPath = Join-Path $saveDir 'Mods\enabled.json'
$enabled = @()
if (Test-Path $enabledPath) {
    try { $enabled = @(Get-Content $enabledPath -Raw | ConvertFrom-Json) } catch { $enabled = @() }
}
if ($enabled -notcontains 'Antfarm') {
    $enabled += 'Antfarm'
    ($enabled | ConvertTo-Json -Compress) | Set-Content $enabledPath -Encoding utf8
    Say 'Enabled the Antfarm mod.' DarkGray
}

# --- start the server ------------------------------------------------

function Port-Open($port) {
    $null -ne (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue)
}

if (Port-Open $GamePort) {
    Say "A server is already running on port $GamePort, using it." DarkGray
}
else {
    $worldDir = Join-Path $saveDir 'Worlds'
    New-Item -ItemType Directory -Force -Path $worldDir | Out-Null
    $world = Join-Path $worldDir "$WorldName.wld"

    $fresh = -not (Test-Path $world)
    if ($fresh) { Say 'Generating a new world. This takes a few minutes the first time.' Yellow }
    else        { Say 'Loading your world.' DarkGray }

    $args = @(
        '-server',
        '-savedirectory', $saveDir,
        '-world', $world,
        '-autocreate', '3',
        '-worldname', $WorldName,
        '-difficulty', '0',
        '-players', '8',
        '-port', "$GamePort",
        '-noupnp'
    )

    Start-Process -FilePath (Join-Path $tml 'start-tModLoaderServer.bat') `
                  -ArgumentList $args -WorkingDirectory $tml -WindowStyle Minimized | Out-Null
}

# --- wait for the colony, then open the panel ------------------------
#
# The panel only answers once the world is loaded and the colony has started,
# so this doubles as the readiness check for everything else.

Say 'Waiting for the colony to wake up...' DarkGray

$panel = "http://localhost:$PanelPort/"
$ready = $false
$deadline = (Get-Date).AddMinutes(12)

while ((Get-Date) -lt $deadline) {
    try {
        Invoke-WebRequest "$panel`stats" -TimeoutSec 4 -UseBasicParsing | Out-Null
        $ready = $true
        break
    } catch { Start-Sleep -Seconds 3 }
}

if (-not $ready) {
    Say 'The server did not come up in time. Check its window for errors.' Red
    exit 1
}

Say 'Colony is running.' Green
Say "Live view: $panel" Cyan
Start-Process $panel

# --- join the world ---------------------------------------------------

Say 'Starting Terraria and joining the world...' DarkGray

Start-Process -FilePath (Join-Path $tml 'start-tModLoader.bat') `
              -ArgumentList @('-join', '127.0.0.1', '-port', "$GamePort") `
              -WorkingDirectory $tml | Out-Null

Write-Host ''
Say 'Done. If Terraria opens on the menu instead of joining, pick your' DarkGray
Say "character and use Multiplayer -> Join via IP -> 127.0.0.1 : $GamePort" DarkGray
Write-Host ''
Say 'Leave the panel open. The world keeps running whether or not you play.' DarkGray
Write-Host ''
Start-Sleep -Seconds 4
