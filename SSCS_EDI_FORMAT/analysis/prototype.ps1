# Prototype converter: proves conversion rules against ALL fixture pairs byte-for-byte.
$ErrorActionPreference = 'Stop'
$root = 'C:\Users\Kashf\Desktop\SSCS_EDI_FORMAT'

function Expand-UpcE([string]$eight) {
    # $eight = 8 digit UPC-E incl leading number system digit and trailing check digit
    $ns = $eight[0]
    if ($ns -ne '0' -and $ns -ne '1') { return $null }
    $d = $eight.Substring(1,6)
    $c = $eight[7]
    $last = $d[5]
    switch -regex ($last) {
        '[012]' { $body = "$ns$($d.Substring(0,2))$last" + '0000' + $d.Substring(2,3) }
        '3'     { $body = "$ns$($d.Substring(0,3))" + '00000' + $d.Substring(3,2) }
        '4'     { $body = "$ns$($d.Substring(0,4))" + '00000' + $d.Substring(4,1) }
        default { $body = "$ns$($d.Substring(0,5))" + '0000' + $last }
    }
    # validate check digit over 11-digit body
    $sum = 0
    for ($i = 0; $i -lt 11; $i++) {
        $v = [int][string]$body[$i]
        if ($i % 2 -eq 0) { $sum += $v * 3 } else { $sum += $v }
    }
    $check = (10 - ($sum % 10)) % 10
    if ("$check" -ne "$c") { return $null }
    return $body
}

function Convert-Code11([string]$code11) {
    # Applies UPC-E expansion when the 11-char SSCS product field holds "8 digits + 3 spaces"
    if ($code11 -match '^\d{8}   $') {
        $eight = $code11.Substring(0,8)
        if ($eight[0] -ne '0' -and $eight[0] -ne '1') {
            # Fixture-proven (SSCS_GHRA_129348 line 104): unsupported number system -> blank field
            return ' ' * 11
        }
        $body = Expand-UpcE $eight
        if ($null -eq $body) { throw "UPC-E validation failed for '$eight'" }
        return $body
    }
    return $code11
}

function Convert-EdiFile([string]$path) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $text = [System.Text.Encoding]::GetEncoding(28591).GetString($bytes)  # latin-1: byte-preserving
    # split preserving EOL
    $lines = [System.Collections.ArrayList]@()
    $i = 0
    while ($i -lt $text.Length) {
        $nl = $text.IndexOf("`n", $i)
        if ($nl -lt 0) { $lines.Add(@($text.Substring($i), '')) | Out-Null; break }
        $end = $nl
        $eol = "`n"
        if ($end -gt $i -and $text[$end-1] -eq "`r") { $end--; $eol = "`r`n" }
        $lines.Add(@($text.Substring($i, $end - $i), $eol)) | Out-Null
        $i = $nl + 1
    }
    $header = $lines[0][0]
    # detect format
    $bLens = @()
    for ($k = 1; $k -lt $lines.Count; $k++) { if ($lines[$k][0].StartsWith('B')) { $bLens += $lines[$k][0].Length } }
    $bLens = $bLens | Sort-Object -Unique
    $isRuby = ($header.Length -eq 64 -and $header.StartsWith('GHRA13') -and $header[6] -ne ' ')
    if ($isRuby) {
        $fmt = 'RubyRed'
    } elseif ($bLens -contains 78) {
        $fmt = 'EDI13'
    } else {
        $fmt = 'EDI'
    }
    $out = [System.Text.StringBuilder]::new()
    for ($k = 0; $k -lt $lines.Count; $k++) {
        $line = $lines[$k][0]; $eol = $lines[$k][1]
        if ($k -eq 0) {
            if ($fmt -eq 'RubyRed') {
                $newLine = 'A' + $line.Substring(6)
            } else {
                $vendorField = $line.Substring(0,7)
                $token = $vendorField.TrimEnd(' ')
                if ($fmt -eq 'EDI13' -and $token.EndsWith('13')) { $token = $token.Substring(0, $token.Length-2) }
                $newLine = 'A' + $token.PadRight(6) + $line.Substring(7)
            }
        } elseif ($line.StartsWith('B')) {
            if ($fmt -eq 'EDI') {
                $code = Convert-Code11 $line.Substring(1,11)
                $newLine = 'B' + $code + $line.Substring(12)
            } elseif ($fmt -eq 'EDI13') {
                $code = Convert-Code11 $line.Substring(1,11)  # first 11 of the 13-char field
                $newLine = 'B' + $code + $line.Substring(14)
            } else { # RubyRed: 79 wide, drop code chars 12-13 and trailing 10-byte suffix '00000000  '
                if ($line.Length -ne 79) { throw "RubyRed B length $($line.Length)" }
                $suffix = $line.Substring(69)
                if ($suffix -ne ('0'*8 + '  ')) { throw "RubyRed suffix mismatch '$suffix'" }
                $code = Convert-Code11 $line.Substring(1,11)
                $newLine = 'B' + $code + $line.Substring(14, 55)
            }
        } elseif ($line.StartsWith('C')) {
            $newLine = $line
        } else {
            throw "Unknown record at line $($k+1): '$line'"
        }
        $out.Append($newLine).Append($eol) | Out-Null
    }
    return ,[System.Text.Encoding]::GetEncoding(28591).GetBytes($out.ToString())
}

function Compare-Bytes([byte[]]$a, [byte[]]$b) {
    if ($a.Length -ne $b.Length) { return "LENGTH DIFF: actual=$($a.Length) expected=$($b.Length)" }
    for ($i = 0; $i -lt $a.Length; $i++) {
        if ($a[$i] -ne $b[$i]) { return "BYTE DIFF at offset $i : actual=0x{0:X2} expected=0x{1:X2}" -f $a[$i], $b[$i] }
    }
    return 'IDENTICAL'
}

$pairs = @(
    @("TXT EDI EXAMPLES (INDUSTRY FORMAT)\EDI_GHRA_118955.txt",  "SSCS EDI EXAMPLES\SSCS_GHRA_118955.txt"),
    @("TXT EDI EXAMPLES (INDUSTRY FORMAT)\EDI_GHRA_120511.txt",  "SSCS EDI EXAMPLES\SSCS_GHRA_120511.txt"),
    @("TXT EDI EXAMPLES (INDUSTRY FORMAT)\EDI_GHRA_125482.txt",  "SSCS EDI EXAMPLES\SSCS_GHRA_125482.txt"),
    @("TXT EDI EXAMPLES (INDUSTRY FORMAT)\EDI_GHRA_129348.txt",  "SSCS EDI EXAMPLES\SSCS_GHRA_129348.txt"),
    @("TXT EDI EXAMPLES (INDUSTRY FORMAT)\EDI_GHRA_129478.txt",  "SSCS EDI EXAMPLES\SSCS_GHRA_129478.txt"),
    @("TXT EDI EXAMPLES (INDUSTRY FORMAT)\EDI_GHRA_129943.txt",  "SSCS EDI EXAMPLES\SSCS_GHRA_129943.txt"),
    @("TXT EDI EXAMPLES (INDUSTRY FORMAT)\EDI_GHRA_131454.txt",  "SSCS EDI EXAMPLES\SSCS_GHRA_131454.txt"),
    @("TXT EDI EXAMPLES (INDUSTRY FORMAT)\EDI_PEPSI-80471906.txt","SSCS EDI EXAMPLES\SSCS_PEPSI-80471906.txt"),
    @("TXT EDI13 EXAMPLES\EDI_GHRA13_118955.txt",  "SSCS EDI EXAMPLES\SSCS_GHRA_118955.txt"),
    @("TXT EDI13 EXAMPLES\EDI_GHRA13_120511.txt",  "SSCS EDI EXAMPLES\SSCS_GHRA_120511.txt"),
    @("TXT EDI13 EXAMPLES\EDI_GHRA13_125482.txt",  "SSCS EDI EXAMPLES\SSCS_GHRA_125482.txt"),
    @("TXT EDI13 EXAMPLES\EDI_GHRA13_129348.txt",  "SSCS EDI EXAMPLES\SSCS_GHRA_129348.txt"),
    @("TXT EDI13 EXAMPLES\EDI_GHRA13_129478.txt",  "SSCS EDI EXAMPLES\SSCS_GHRA_129478.txt"),
    @("TXT EDI13 EXAMPLES\EDI_GHRA13_129943.txt",  "SSCS EDI EXAMPLES\SSCS_GHRA_129943.txt"),
    @("TXT EDI13 EXAMPLES\EDI_GHRA13_131454.txt",  "SSCS EDI EXAMPLES\SSCS_GHRA_131454.txt"),
    @("TXT EDI13 EXAMPLES\EDI13-RubyRedSalesServices.txt", "SSCS EDI EXAMPLES\EDI-RubyRedSalesServices (SSCS EXAMPLE).txt")
)

$pass = 0; $fail = 0
foreach ($p in $pairs) {
    $inPath = Join-Path $root $p[0]
    $expPath = Join-Path $root $p[1]
    try {
        $actual = Convert-EdiFile $inPath
        $expected = [System.IO.File]::ReadAllBytes($expPath)
        $r = Compare-Bytes $actual $expected
    } catch {
        $r = "ERROR: $($_.Exception.Message)"
    }
    if ($r -eq 'IDENTICAL') { $pass++ } else { $fail++ }
    "{0,-55} -> {1,-50} : {2}" -f (Split-Path $p[0] -Leaf), (Split-Path $p[1] -Leaf), $r
}
"`nPASS=$pass FAIL=$fail"
