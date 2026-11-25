#Requires -Version 7.3
[CmdletBinding()]
param(
    [Parameter(Mandatory, ValueFromRemainingArguments)]
    [string[]]$Path
)

$ErrorActionPreference = 'Stop'

$problems = @()
$signers = @{}
foreach ($file in $Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $file
    if ($signature.Status -eq 'NotSigned') {
        $problems += "$file isn't signed"
    } elseif ($signature.Status -ne 'Valid') {
        $problems += "$file's signature isn't trusted ($($signature.Status): $($signature.StatusMessage))"
    } elseif (-not $signature.TimeStamperCertificate) {
        $problems += "$file's signature has no timestamp"
    } else {
        $signers[$signature.SignerCertificate.Subject] = $signature.SignerCertificate
    }
}
if ($signers.Count -gt 1) {
    $problems += "The files have different signers: $($signers.Keys -join ' | ')"
}
if ($problems) {
    $problems | ForEach-Object { Write-Host "::error::$_" }
    exit 1
}

$certificate = @($signers.Values)[0]
$name = $certificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
Write-Host "$($Path.Count) signed and timestamped, by $name ($($certificate.Subject))"
if ($env:GITHUB_OUTPUT) {
    "signer=$name" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8NoBOM
}
exit 0
