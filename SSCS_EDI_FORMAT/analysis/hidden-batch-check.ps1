# Runs the PRODUCTION conversion core over every hidden batch file and compares
# the result byte-for-byte with the supplied SSCS reference.
$ErrorActionPreference = 'Stop'
$root = 'C:\Users\Kashf\Desktop\SSCS_EDI_FORMAT'
Add-Type -Path "$root\converter\src\EdiToSscs.Core\bin\Release\net8.0\EdiToSscs.Core.dll"

function Check($inputDir, $label) {
    $pass = 0; $fail = 0
    Get-ChildItem "$root\$inputDir" -Filter *.txt | Sort-Object Name | ForEach-Object {
        $name = $_.Name
        $invoice = $name -replace '^EDI_GHRA13_|^EDI_GHRA_', '' -replace '\.txt$', ''
        $expPath = "$root\Hidden batch sscs examples\SSCS_GHRA_$invoice.txt"
        $inputBytes = [System.IO.File]::ReadAllBytes($_.FullName)
        try {
            $result = [EdiToSscs.Core.EdiConverter]::Convert($inputBytes)
            $actual = $result.OutputBytes
            $expected = [System.IO.File]::ReadAllBytes($expPath)
            if ($actual.Length -ne $expected.Length) {
                $script:fail++
                "$label $name : LENGTH DIFF actual=$($actual.Length) expected=$($expected.Length) fmt=$($result.Format)"
            } else {
                $diff = -1
                for ($i = 0; $i -lt $actual.Length; $i++) { if ($actual[$i] -ne $expected[$i]) { $diff = $i; break } }
                if ($diff -ge 0) {
                    $script:fail++
                    # line/col
                    $line = 1; $ls = 0
                    for ($j = 0; $j -lt $diff; $j++) { if ($expected[$j] -eq 10) { $line++; $ls = $j + 1 } }
                    "$label $name : BYTE DIFF off=$diff line=$line col=$($diff-$ls+1) actual=0x{0:X2}'{1}' expected=0x{2:X2}'{3}' fmt=$($result.Format) warn=$($result.Warnings.Count)" -f $actual[$diff], [char]$actual[$diff], $expected[$diff], [char]$expected[$diff]
                } else {
                    $script:pass++
                    $w = if ($result.HasWarnings) { " (warnings: $($result.Warnings.Count))" } else { "" }
                    "$label $name : IDENTICAL fmt=$($result.Format)$w"
                }
            }
        } catch {
            $script:fail++
            $msg = $_.Exception.InnerException ?? $_.Exception
            "$label $name : ERROR $($msg.Message)"
        }
    }
}

$script:pass = 0; $script:fail = 0
Check 'Hidden EDI batch examples' 'EDI  '
""
Check 'Hidden EDI13 batch examples' 'EDI13'
""
"TOTAL: PASS=$script:pass FAIL=$script:fail"
