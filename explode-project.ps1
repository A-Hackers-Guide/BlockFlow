# Explodes a BlockFlow project (.exe or .bflow) into a big, structured folder tree:
# one folder per block (nested inside its If / Repeat), one file per setting, plus indexes,
# a manifest, checksums, the packed copy-folder files and the raw project JSON.
# Usage: powershell -File explode-project.ps1 -Project "C:\...\My Flow.exe" [-Out "C:\...\folder"]
param(
    [Parameter(Mandatory)] [string]$Project,
    [string]$Out
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Write-Text([string]$path, [string]$text) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path, $text, $utf8)
}
function Safe([string]$s) {
    $bad = [IO.Path]::GetInvalidFileNameChars()
    $r = -join ($s.ToCharArray() | ForEach-Object { if ($bad -contains $_) { '_' } else { $_ } })
    if ($r.Length -gt 40) { $r = $r.Substring(0, 40) }
    $r.Trim().TrimEnd('.')
}

# ---------------------------------------------------------------- read the project
$zipBytes = $null
if ($Project -like '*.exe') {
    $f = [IO.File]::Open($Project, 'Open', 'Read', 'ReadWrite')
    try {
        $len = $f.Length
        $tail = New-Object byte[] 32
        $f.Seek(-32, 'End') | Out-Null; $f.Read($tail, 0, 32) | Out-Null
        $magic = [Text.Encoding]::ASCII.GetString($tail, 16, 16)
        if ($magic -eq 'BLOCKFLOW-FLOW02') {
            $jsonLen = [BitConverter]::ToInt64($tail, 0); $zipLen = [BitConverter]::ToInt64($tail, 8); $trailer = 32
        } elseif ([Text.Encoding]::ASCII.GetString($tail, 16, 16) -ne '' -and [Text.Encoding]::ASCII.GetString($tail, 16, 16) -eq 'BLOCKFLOW-FLOW01') {
            $jsonLen = [BitConverter]::ToInt64($tail, 8); $zipLen = 0; $trailer = 24
        } else { throw "That .exe doesn't contain a BlockFlow project." }
        $start = $len - $trailer - $zipLen - $jsonLen
        $jb = New-Object byte[] $jsonLen
        $f.Seek($start, 'Begin') | Out-Null; $f.Read($jb, 0, $jsonLen) | Out-Null
        $json = $utf8.GetString($jb)
        if ($zipLen -gt 0) { $zipBytes = New-Object byte[] $zipLen; $f.Read($zipBytes, 0, $zipLen) | Out-Null }
    } finally { $f.Close() }
} else {
    $json = [IO.File]::ReadAllText($Project)
}
$flow = $json | ConvertFrom-Json
if (-not $Out) { $Out = Join-Path ([IO.Path]::GetDirectoryName($Project)) ((Safe $flow.Name) + " - extracted") }
if (Test-Path $Out) { throw "Output folder already exists: $Out" }

# ---------------------------------------------------------------- structure (nesting)
$blocks = @($flow.Blocks)
$stack = New-Object System.Collections.Stack
$root = Join-Path $Out "flow\000_main"
$cur = $root
$rows = New-Object System.Collections.Generic.List[string]
$rows.Add("index,type,enabled,depth,folder")
$tree = New-Object System.Text.StringBuilder
$typeCount = @{}
$vars = New-Object System.Collections.Generic.SortedSet[string]
$fieldFiles = 0

for ($i = 0; $i -lt $blocks.Count; $i++) {
    $b = $blocks[$i]
    $type = [string]$b.Type
    $typeCount[$type] = 1 + [int]$typeCount[$type]
    $num = '{0:D4}' -f ($i + 1)

    # closers / else move back out before writing
    if ($type -in 'EndIf', 'EndRepeat') { if ($stack.Count) { $cur = $stack.Pop() } }
    if ($type -eq 'Else' -and $stack.Count) { $cur = Join-Path ([IO.Path]::GetDirectoryName($cur)) "else" }

    $label = $type
    if ($b.Values -and $b.Values.var) { $label += "_" + (Safe ([string]$b.Values.var)) }
    $dir = Join-Path $cur "$num`_$label"
    $depth = $stack.Count
    $enabled = -not [bool]$b.Disabled

    # block.json + meta
    Write-Text (Join-Path $dir "block.json") ($b | ConvertTo-Json -Depth 10)
    Write-Text (Join-Path $dir "meta\index.txt") ($i + 1)
    Write-Text (Join-Path $dir "meta\type.txt") $type
    Write-Text (Join-Path $dir "meta\enabled.txt") $enabled
    Write-Text (Join-Path $dir "meta\depth.txt") $depth
    Write-Text (Join-Path $dir "meta\previous.txt") ($(if ($i -gt 0) { [string]$blocks[$i - 1].Type } else { "(start)" }))
    Write-Text (Join-Path $dir "meta\next.txt") ($(if ($i -lt $blocks.Count - 1) { [string]$blocks[$i + 1].Type } else { "(end)" }))

    # one file per setting
    if ($b.Values) {
        foreach ($p in $b.Values.PSObject.Properties) {
            Write-Text (Join-Path $dir ("fields\" + (Safe $p.Name) + ".txt")) ([string]$p.Value)
            $fieldFiles++
            foreach ($m in [regex]::Matches([string]$p.Value, '\{([A-Za-z_][A-Za-z0-9_ ]*?)(?::[^{}]*)?\}')) { [void]$vars.Add($m.Groups[1].Value.Trim()) }
        }
        if ($b.Values.var -and $type -notin 'If') { [void]$vars.Add(([string]$b.Values.var).Trim('{', '}', ' ')) }
    }

    $rows.Add("$($i + 1),$type,$enabled,$depth,`"$($dir.Substring($Out.Length + 1))`"")
    [void]$tree.AppendLine(('    ' * $depth) + "$num  $type" + $(if (-not $enabled) { "  (off)" } else { "" }))

    # containers: children go inside a "then"/"body" folder
    if ($type -eq 'If') { $stack.Push($cur); $cur = Join-Path $dir "then" }
    elseif ($type -eq 'Repeat') { $stack.Push($cur); $cur = Join-Path $dir "body" }
}

# ---------------------------------------------------------------- settings, indexes, manifest
Write-Text (Join-Path $Out "settings\name.txt") ([string]$flow.Name)
Write-Text (Join-Path $Out "settings\show_done_message.txt") ([string]$flow.ShowDoneMessage)
Write-Text (Join-Path $Out "settings\copy_folder.txt") ([string]$flow.CopyFolder)
Write-Text (Join-Path $Out "settings\format_version.txt") ([string]$flow.Version)
Write-Text (Join-Path $Out "index\blocks.csv") ($rows -join "`r`n")
Write-Text (Join-Path $Out "index\tree.txt") $tree.ToString()
Write-Text (Join-Path $Out "index\variables.txt") (($vars | ForEach-Object { "{$_}" }) -join "`r`n")
Write-Text (Join-Path $Out "index\block_types.txt") (($typeCount.GetEnumerator() | Sort-Object Name | ForEach-Object { "{0,-20} {1}" -f $_.Name, $_.Value }) -join "`r`n")
foreach ($t in $typeCount.Keys) {
    $list = for ($i = 0; $i -lt $blocks.Count; $i++) { if ($blocks[$i].Type -eq $t) { '{0:D4}' -f ($i + 1) } }
    Write-Text (Join-Path $Out "index\by_type\$t.txt") ($list -join "`r`n")
}
Write-Text (Join-Path $Out "raw\project.json") $json

# packed copy-folder files
$assetCount = 0
if ($zipBytes) {
    $zip = New-Object System.IO.Compression.ZipArchive((New-Object IO.MemoryStream(, $zipBytes)))
    foreach ($e in $zip.Entries) {
        if ([string]::IsNullOrEmpty($e.Name)) { continue }
        $target = Join-Path $Out ("assets\" + $e.FullName.Replace('/', '\'))
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        $in = $e.Open(); $outS = [IO.File]::Create($target); $in.CopyTo($outS); $outS.Close(); $in.Close()
        $assetCount++
    }
    $zip.Dispose()
}

$manifest = [ordered]@{
    project       = [string]$flow.Name
    extractedFrom = $Project
    extractedAt   = (Get-Date).ToString('s')
    blocks        = $blocks.Count
    settingFiles  = $fieldFiles
    variables     = $vars.Count
    packedFiles   = $assetCount
}
Write-Text (Join-Path $Out "manifest.json") ($manifest | ConvertTo-Json)
Write-Text (Join-Path $Out "README.txt") @"
$($flow.Name) — extracted BlockFlow project

flow\        every block as a folder, in run order (0001, 0002, ...). Blocks inside an If are in its
             then\ and else\ folders; blocks inside a Repeat are in its body\ folder.
             Each block has block.json, meta\ (type, position, neighbours) and fields\ (one file per setting).
settings\    the project's own settings.
index\       blocks.csv, tree.txt (the whole flow as an outline), variables.txt, block types.
assets\      the copy-folder files that were packed inside the .exe.
raw\         the complete project exactly as BlockFlow stores it (this is what BlockFlow can open).
checksums.sha256   a SHA-256 fingerprint of every file.

To open this project in BlockFlow again, open the original .exe (or rename raw\project.json to .bflow).
"@

# checksums for everything
$sums = Get-ChildItem $Out -Recurse -File | Where-Object { $_.Name -ne 'checksums.sha256' } | ForEach-Object {
    (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower() + "  " + $_.FullName.Substring($Out.Length + 1)
}
Write-Text (Join-Path $Out "checksums.sha256") ($sums -join "`r`n")

$files = (Get-ChildItem $Out -Recurse -File).Count
$folders = (Get-ChildItem $Out -Recurse -Directory).Count
"Extracted to: $Out"
"$files files in $folders folders  ($($blocks.Count) blocks, $fieldFiles setting files, $assetCount packed files)"
