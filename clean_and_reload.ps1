$ErrorActionPreference = "Stop"

$Vm = "luonggminh05@192.168.18.129"
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

Write-Host "4. Waiting for api-server to be ready..."
Start-Sleep -Seconds 10

Write-Host "5. Copying clean Vietnamese files from Windows to VM..."
scp -r f:\BKU\Intern\Host\papers\* $Vm`:/opt/papers/

Write-Host "6. Restarting file-watcher to process only the new files..."
ssh -t $Vm "$Kubectl delete pod -l app=file-watcher --ignore-not-found=true"

Write-Host "Done."
