$ErrorActionPreference = "Stop"

Write-Host "1. Copying updated files to VM..."
scp f:\BKU\Intern\Host\Dockerfile.sql luonggminh05@192.168.18.129:/home/luonggminh05/app_code/
scp f:\BKU\Intern\Host\Dockerfile luonggminh05@192.168.18.129:/home/luonggminh05/app_code/
scp f:\BKU\Intern\Host\requirements.txt luonggminh05@192.168.18.129:/home/luonggminh05/app_code/
scp f:\BKU\Intern\Host\main.py luonggminh05@192.168.18.129:/home/luonggminh05/app_code/
scp f:\BKU\Intern\Host\ingest_watcher.py luonggminh05@192.168.18.129:/home/luonggminh05/app_code/
scp f:\BKU\Intern\Host\k8s\sqlserver.yaml luonggminh05@192.168.18.129:/home/luonggminh05/app_code/k8s/

Write-Host "2. Dropping old Database table..."
ssh -t luonggminh05@192.168.18.129 "POD=`$(sudo /var/lib/rancher/rke2/bin/kubectl get pod -l app=sqlserver -o jsonpath='{.items[0].metadata.name}'); if [ -n `"`$POD`" ]; then sudo /var/lib/rancher/rke2/bin/kubectl exec `"`$POD`" -- /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'YourStrong!Passw0rd' -C -Q 'DROP TABLE IF EXISTS Documents'; else echo 'No sqlserver pod found, skipping drop'; fi"

Write-Host "3. Building SQL Server FTS Image & API Server Image..."
ssh -t luonggminh05@192.168.18.129 "cd /home/luonggminh05/app_code && sudo docker build -t sqlserver-fts:latest -f Dockerfile.sql . && sudo docker build -t api_server:latest -t file_watcher:latest . && sudo docker save sqlserver-fts:latest api_server:latest file_watcher:latest | sudo /var/lib/rancher/rke2/bin/ctr -a /run/k3s/containerd/containerd.sock -n k8s.io images import -"

Write-Host "4. Deploying new SQL Server with FTS..."
ssh -t luonggminh05@192.168.18.129 "sudo /var/lib/rancher/rke2/bin/kubectl apply -f /home/luonggminh05/app_code/k8s/sqlserver.yaml"

Write-Host "5. Restarting Pods..."
ssh -t luonggminh05@192.168.18.129 "sudo /var/lib/rancher/rke2/bin/kubectl delete pod -l app=sqlserver && sudo /var/lib/rancher/rke2/bin/kubectl delete pod -l app=api-server && sudo /var/lib/rancher/rke2/bin/kubectl delete pod -l app=file-watcher"

Write-Host "Done! Please wait 30 seconds for SQL Server to boot up, then run the load test!"
