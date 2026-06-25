$ErrorActionPreference = "Stop"

$Vm = "user@<vm-ip>"
$RemoteApp = "/home/user/app_code"
$Kubectl = "sudo /var/lib/rancher/rke2/bin/kubectl"
$Ctr = "sudo /var/lib/rancher/rke2/bin/ctr -a /run/k3s/containerd/containerd.sock -n k8s.io"
$EnvFile = Join-Path $PSScriptRoot ".env.ps1"

if (Test-Path -LiteralPath $EnvFile) {
    . $EnvFile
}

function Get-SqlUser {
    if (-not [string]::IsNullOrWhiteSpace($env:SQLSERVER_USER)) {
        return $env:SQLSERVER_USER
    }

    return Read-Host "Enter SQL Server user"
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

$SqlUser = Get-SqlUser
$SqlPassword = Get-SqlPassword
if ($SqlPassword.Contains("'")) {
    throw "SQL Server password must not contain a single quote for this deploy script."
}

$SqlConnectionString = "Server=sqlserver;Database=master;User Id=$SqlUser;Password=$SqlPassword;TrustServerCertificate=True;"
$RemoteSqlPassword = "'$SqlPassword'"
$RemoteConnectionString = "'$SqlConnectionString'"

Write-Host "1. Copying deployment files to VM..."
ssh -t $Vm "mkdir -p $RemoteApp/src $RemoteApp/k8s"
scp -r "$PSScriptRoot\src\RagApi" $Vm`:$RemoteApp/src/
scp "$PSScriptRoot\.dockerignore" $Vm`:$RemoteApp/
scp "$PSScriptRoot\Dockerfile.sql" $Vm`:$RemoteApp/
scp "$PSScriptRoot\Dockerfile" $Vm`:$RemoteApp/
scp "$PSScriptRoot\k8s\tei.yaml" $Vm`:$RemoteApp/k8s/
scp "$PSScriptRoot\k8s\sqlserver.yaml" $Vm`:$RemoteApp/k8s/
scp "$PSScriptRoot\k8s\api-server-dotnet.yaml" $Vm`:$RemoteApp/k8s/

Write-Host "2. Creating or updating Kubernetes Secret..."
ssh -t $Vm "$Kubectl create secret generic sqlserver-secret --from-literal=sa-password=$RemoteSqlPassword --from-literal=connection-string=$RemoteConnectionString --dry-run=client -o yaml | $Kubectl apply -f -"

Write-Host "3. Building images and importing them into RKE2 containerd..."
ssh -t $Vm "cd $RemoteApp && sudo docker build -t sqlserver-fts:latest -f Dockerfile.sql . && sudo docker build -t rag_api_dotnet:latest -f Dockerfile . && sudo docker save sqlserver-fts:latest rag_api_dotnet:latest -o images.tar && $Ctr images import images.tar && sudo rm -f images.tar"

Write-Host "4. Applying Kubernetes manifests..."
ssh -t $Vm "$Kubectl apply -f $RemoteApp/k8s/tei.yaml"
ssh -t $Vm "$Kubectl apply -f $RemoteApp/k8s/sqlserver.yaml"
ssh -t $Vm "$Kubectl apply -f $RemoteApp/k8s/api-server-dotnet.yaml"

Write-Host "5. Restarting application pods..."
ssh -t $Vm "$Kubectl delete pod -l app=api-server --ignore-not-found=true"

Write-Host "Done. The service keeps NodePort 30001 and cluster port 8001."
