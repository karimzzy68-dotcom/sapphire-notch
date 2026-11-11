# Run before giving anyone an installer, zip or build folder:
#   powershell -ExecutionPolicy Bypass -File .\Check-Release.ps1 -Path "bin\Release\net8.0-windows10.0.19041.0"
# Fails (exit 1) if the folder holds keys, tokens, login files or personal data. Prints file names only, never the secret.
param([string]$Path = "bin\Release\net8.0-windows10.0.19041.0")

$badNames = 'openrouter\.key|auth\.json|\.credentials\.json|accounts\.json|settings\.json|dictation\.json|notes\.json|notes\.txt|snippets\.json|dictionary\.json|errors\.log|\.env$|\.pfx$|\.pem$'
$patterns = 'sk-or-[A-Za-z0-9]{10,}|sk-[A-Za-z0-9]{20,}|ghp_[A-Za-z0-9]{20,}|gho_[A-Za-z0-9]{20,}|eyJ[A-Za-z0-9_-]{30,}\.[A-Za-z0-9_-]{10,}|refresh_token"\s*:\s*"[^"]{10}'
$problems = 0

foreach ($file in Get-ChildItem -LiteralPath $Path -Recurse -File -Force) {
    if ($file.Name -match $badNames) {
        "PERSONAL FILE: $($file.FullName)"; $problems++; continue
    }
    if ($file.Length -lt 5MB -and $file.Extension -match '^\.(json|txt|md|xml|config|cs|xaml|log|ini|toml|yaml|yml|js)$') {
        if (Select-String -LiteralPath $file.FullName -Pattern $patterns -Quiet) {
            "SECRET-LIKE TEXT: $($file.FullName)"; $problems++
        }
    }
}

if ($problems -gt 0) { "$problems problem(s). Do not ship this folder."; exit 1 }
"Clean: no keys, tokens, login files or personal data found in $Path"
