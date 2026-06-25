$ErrorActionPreference = "Stop"

$Vm = "user@<vm-ip>"
$Kubectl = "sudo /var/lib/rancher/rke2/bin/kubectl"
$EnvFile = Join-Path $PSScriptRoot ".env.ps1"

if (Test-Path -LiteralPath $EnvFile) {
    . $EnvFile
}

function Get-SqlPassword {
    if (-not [string]::IsNullOrWhiteSpace($env:SQLSERVER_SA_PASSWORD)) {
        return $env:SQLSERVER_SA_PASSWORD
    }

    $securePassword = Read-Host "Enter SQL Server sa password" -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

$SqlPassword = Get-SqlPassword
if ($SqlPassword.Contains("'")) {
    throw "SQL Server password must not contain a single quote for this script."
}

Write-Host "1. Wiping all files in /opt/papers/ on VM..."
ssh -t $Vm "sudo rm -rf /opt/papers/*"

Write-Host "2. Dropping Documents table in SQL Server..."
ssh -t $Vm "POD=`$($Kubectl get pod -l app=sqlserver -o jsonpath='{.items[0].metadata.name}'); if [ -n `"`$POD`" ]; then $Kubectl exec `"`$POD`" -- /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P '$SqlPassword' -C -Q 'DROP TABLE IF EXISTS Documents'; fi"

Write-Host "3. Restarting api-server to recreate table..."
ssh -t $Vm "$Kubectl delete pod -l app=api-server --ignore-not-found=true"

Write-Host "4. Waiting for api-server rollout to be ready..."
ssh -t $Vm "$Kubectl rollout status deployment/api-server --timeout=180s"
Start-Sleep -Seconds 5

Write-Host "5. Copying clean Vietnamese files from Windows to VM..."
scp -r f:\BKU\Intern\Host\papers\* $Vm`:/opt/papers/

Write-Host "6. Verifying papers and triggering watcher events..."
ssh -t $Vm "ls -lah /opt/papers && find /opt/papers -type f -exec touch {} \;"

Write-Host "7. Showing recent ingestion logs..."
ssh -t $Vm "$Kubectl logs -l app=api-server --tail=120 | grep -E 'Watching directory|Processing file|Created .* chunks|Successfully ingested|No content extracted|Error ingesting|Vision caption|Ingestion' || true"

Write-Host "Done."
