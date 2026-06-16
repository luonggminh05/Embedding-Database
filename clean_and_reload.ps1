Write-Host "1. Wiping all files in /opt/papers/ on VM..."
ssh -t luonggminh05@192.168.18.129 "sudo rm -rf /opt/papers/*"

Write-Host "2. Dropping Documents table in SQL Server..."
ssh -t luonggminh05@192.168.18.129 "POD=`$(sudo /var/lib/rancher/rke2/bin/kubectl get pod -l app=sqlserver -o jsonpath='{.items[0].metadata.name}'); if [ -n `"`$POD`" ]; then sudo /var/lib/rancher/rke2/bin/kubectl exec `"`$POD`" -- /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'YourStrong!Passw0rd' -C -Q 'DROP TABLE IF EXISTS Documents'; fi"

Write-Host "3. Restarting api-server to recreate table..."
ssh -t luonggminh05@192.168.18.129 "sudo /var/lib/rancher/rke2/bin/kubectl delete pod -l app=api-server"

Write-Host "4. Waiting for api-server to be ready..."
Start-Sleep -Seconds 10

Write-Host "5. Copying clean Vietnamese files from Windows to VM..."
scp -r f:\BKU\Intern\Host\papers\* luonggminh05@192.168.18.129:/opt/papers/

Write-Host "6. Restarting file-watcher to process ONLY the new files..."
ssh -t luonggminh05@192.168.18.129 "sudo /var/lib/rancher/rke2/bin/kubectl delete pod -l app=file-watcher"

Write-Host "Done!"
