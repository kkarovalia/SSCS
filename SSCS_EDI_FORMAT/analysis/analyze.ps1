# Byte-level analysis of EDI / EDI13 / SSCS fixture files
$ErrorActionPreference = 'Stop'
$root = 'C:\Users\Kashf\Desktop\SSCS_EDI_FORMAT'

function Analyze-File([string]$path) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $n = $bytes.Length
    $bom = ($n -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $crlf = 0; $lf = 0; $cr = 0
    for ($i = 0; $i -lt $n; $i++) {
        if ($bytes[$i] -eq 13) {
            if ($i + 1 -lt $n -and $bytes[$i+1] -eq 10) { $crlf++; $i++ } else { $cr++ }
        } elseif ($bytes[$i] -eq 10) { $lf++ }
    }
    $finalNl = ($n -gt 0 -and ($bytes[$n-1] -eq 10 -or $bytes[$n-1] -eq 13))
    # line lengths (content without EOL)
    $lines = @()
    $start = 0
    for ($i = 0; $i -lt $n; $i++) {
        if ($bytes[$i] -eq 10) {
            $end = $i
            if ($end -gt $start -and $bytes[$end-1] -eq 13) { $end-- }
            $lines += ,@($start, ($end - $start))
            $start = $i + 1
        }
    }
    if ($start -lt $n) { $lines += ,@($start, ($n - $start)) ; $trailingNoNl = $true } else { $trailingNoNl = $false }
    $nonAscii = 0
    foreach ($b in $bytes) { if ($b -gt 126 -or ($b -lt 32 -and $b -ne 10 -and $b -ne 13)) { $nonAscii++ } }
    $rec = @{}
    foreach ($ln in $lines) {
        $off = $ln[0]; $len = $ln[1]
        $t = if ($len -gt 0) { [char]$bytes[$off] } else { '<empty>' }
        $key = "$t/$len"
        if (-not $rec.ContainsKey($key)) { $rec[$key] = 0 }
        $rec[$key]++
    }
    $recStr = ($rec.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Key)x$($_.Value)" }) -join ' '
    "{0}`n  bytes={1} BOM={2} CRLF={3} LF-only={4} CR-only={5} finalEOL={6} lastLineNoEOL={7} nonAsciiOrCtl={8}`n  lines={9}  recType/len={10}" -f `
        (Split-Path $path -Leaf), $n, $bom, $crlf, $lf, $cr, $finalNl, $trailingNoNl, $nonAscii, $lines.Count, $recStr
}

Get-ChildItem -Path $root -Recurse -Filter *.txt | Where-Object { $_.FullName -notmatch '\\analysis\\' } | Sort-Object FullName | ForEach-Object {
    Analyze-File $_.FullName
    ""
}
