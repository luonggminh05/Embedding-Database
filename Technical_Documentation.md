# 1. Tài liệu Embedding Model

## 1. Mục tiêu

- Triển khai Embedding Model tự lưu trữ (Self-hosted) trên nền tảng Kubernetes.
- Cung cấp API sinh vector.

## 2. Kiến trúc

```text
Client (Postman/Web)
   |
   v
API Server (ASP.NET Core)
   |
   v
Embedding Server (TEI Pod)
   |
   v
SQL Server 2025 (Vector Database)
```

## 3. Công nghệ sử dụng

- **Nền tảng:** Kubernetes (RKE2), Rancher
- **Engine:** Text Embeddings Inference (TEI) của HuggingFace. Viết bằng ngôn ngữ Rust.
- **Docker Image:** `ghcr.io/huggingface/text-embeddings-inference:cpu-1.2`
- **Embedding Model:** `BAAI/bge-m3` (Model hỗ trợ đa ngôn ngữ, sinh ra vector 1024 chiều, hiểu Tiếng Việt cực tốt).
- **Ngôn ngữ kết nối:** C# (ASP.NET Core) cho API trung tâm; Python giữ lại cho File Watcher doc loader.

## 4. Quy trình hoạt động

1. Người dùng/Hệ thống nạp text (từ file hoặc câu hỏi query).
2. API Server nhận request và chuyển tiếp dạng mảng (batch) sang TEI Server.
3. Model xử lý ngôn ngữ tự nhiên, tự động chuẩn hóa (normalize) và sinh ra ma trận vector 1024 chiều.
4. Trả mảng vector về cho API Server để lưu hoặc so sánh khoảng cách.

**Ví dụ Request gửi vào TEI:**

```json
{
  "inputs": ["What is Dark Matter?"],
  "normalize": true,
  "truncate": true
}
```

**Ví dụ Output trả về:**

```json
[
  [0.012, -0.045, 0.108, ...]
]
```

## 5. Cấu hình Kubernetes

**Deployment (`tei.yaml`):**

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: embedding-server
spec:
  replicas: 1
  template:
    spec:
      containers:
        - name: tei
          image: ghcr.io/huggingface/text-embeddings-inference:cpu-1.2
          command:
            [
              "text-embeddings-router",
              "--model-id",
              "BAAI/bge-m3",
              "--port",
              "80",
            ]
          volumeMounts:
            - name: model-data
              mountPath: /data
```

**Service:**

```yaml
apiVersion: v1
kind: Service
metadata:
  name: embedding-server
spec:
  type: NodePort
  ports:
    - port: 80
      nodePort: 30080
```

## 6. Tài nguyên sử dụng

| Resource     | Value             | Ghi chú                                                                            |
| :----------- | :---------------- | :--------------------------------------------------------------------------------- |
| **CPU/RAM**  | Tự động điều tiết | Chạy hoàn toàn trên CPU (cpu-1.2)                                                  |
| **Storage**  | 5GB PVC           | Dùng để cache Model tải từ HuggingFace Hub, tránh phải tải lại mỗi khi restart Pod |
| **Replicas** | 1                 | Có thể scale thêm nếu tải cao                                                      |

## 7. Cách kiểm tra

Lệnh kiểm tra trạng thái trên K8s:

```bash
kubectl get pods -l app=embedding-server
kubectl get svc embedding-server
```

Kiểm tra API có hoạt động không:

```bash
curl -X POST http://192.168.18.129:30080/embed \
    -H 'Content-Type: application/json' \
    -d '{"inputs": ["test"]}'
```

---

# 2. Tài liệu SQL Server trên Kubernetes

## 1. Mục tiêu

- Triển khai SQL Server 2025 dưới dạng Pod trong Kubernetes.
- Dùng tính năng **Native Vector Support** của SQL Server 2025 để có một Vector Database chuyên dụng.

## 2. Kiến trúc

```text
API Server (C# / .NET)
     |
     v
 SQL Service (ClusterIP: 1433)
     |
     v
 SQL Server 2025 Pod
     |
     v
 Persistent Volume (10GB)
```

## 3. Thành phần

- **Engine:** SQL Server 2025 Developer Edition (có hỗ trợ Full-Text Search)
- **Docker Image:** `sqlserver-fts:latest` (Custom Image build từ `Dockerfile.sql`)
- **Nền tảng:** Kubernetes (RKE2), Rancher
- **Lưu trữ:** Persistent Volume (PV) và Persistent Volume Claim (PVC) dùng `local-path`.

## 4. Cấu hình

**PVC (`sqlserver.yaml`):**

```yaml
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: sql-data-pvc
spec:
  storageClassName: local-path
  accessModes: [ReadWriteOnce]
  resources:
    requests:
      storage: 10Gi
```

**Deployment:**

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: sqlserver
spec:
  replicas: 1
  template:
    spec:
      containers:
        - name: sqlserver
          image: sqlserver-fts:latest
          env:
            - name: ACCEPT_EULA
              value: "Y"
            - name: MSSQL_SA_PASSWORD
              valueFrom:
                secretKeyRef:
                  name: sqlserver-secret
                  key: sa-password
          volumeMounts:
            - name: sql-data
              mountPath: /var/opt/mssql
```

## 5. Thông tin kết nối

- **Host nội bộ (Trong K8s):** `sqlserver`
- **Port nội bộ:** `1433`
- **NodePort (Truy cập từ Windows):** `31433` (IP: `192.168.18.129,31433`)
- **Database:** `master`
- **Table:** `Documents` (Có cột `embedding VECTOR(1024)`)

## 6. Kiểm tra

Lệnh kiểm tra Pod và Logs:

```bash
kubectl get pods -l app=sqlserver
kubectl logs -l app=sqlserver
```

Kết nối thử bằng công cụ:

- Dùng SQL Server Management Studio (SSMS) hoặc VS Code mssql extension.
- Server Name: `192.168.18.129,31433` (User: `sa`, password lấy từ Secret `sqlserver-secret` hoặc biến môi trường `SQLSERVER_SA_PASSWORD` khi deploy).

## 7. Sao lưu và phục hồi

**Backup (T-SQL):**

```sql
BACKUP DATABASE master
TO DISK = '/var/opt/mssql/data/master_backup.bak'
```

_(Do file backup nằm trong thư mục `/var/opt/mssql/data`, nên đã được PVC lưu trữ vĩnh viễn)._

**Restore (T-SQL):**

```sql
RESTORE DATABASE master
FROM DISK = '/var/opt/mssql/data/master_backup.bak'
WITH REPLACE
```

---

# 3. Vận hành & Cập nhật Hệ thống

## 1. Nạp tài liệu mới vào hệ thống

Sử dụng File Watcher để tự động nhận diện file. Để nạp tài liệu (PDF, DOCX, TXT...), copy file đó từ máy Windows sang thư mục `/opt/papers` của máy ảo Ubuntu bằng lệnh `scp`.

**Mở PowerShell trên Windows và chạy lệnh:**

```powershell
# Nạp 1 file
scp D:\TaiLieu\AI_Research.pdf luonggminh05@192.168.18.129:/opt/papers/

# Nạp toàn bộ file trong folder
scp D:\TaiLieu\* luonggminh05@192.168.18.129:/opt/papers/
```

## 2. Cập nhật Code & Deploy Hệ thống

Để đơn giản hóa quá trình cập nhật mã nguồn (ASP.NET Core, File Watcher) và tự động build các Docker Image (bao gồm cả Image SQL Server có Full-Text Search), bạn có thể sử dụng script PowerShell đã được chuẩn bị sẵn.

**Mở PowerShell trên Windows và chạy lệnh:**

```powershell
.\deploy_dotnet.ps1
```

Script này sẽ tự động thực hiện các bước sau:

1. Copy các file code mới (`src/RagApi`, `ingest_watcher.py`, `Dockerfile*`, `requirements.txt`, `yaml`...) sang máy ảo Ubuntu.
2. Tạo/cập nhật Kubernetes Secret `sqlserver-secret` từ biến môi trường `SQLSERVER_SA_PASSWORD` hoặc mật khẩu bạn nhập khi chạy script.
3. Build lại các Docker Image: `sqlserver-fts:latest`, `rag_api_dotnet:latest`, `file_watcher:latest` và nạp vào Kubernetes.
4. Deploy các manifest `tei.yaml`, `sqlserver.yaml`, `api-server-dotnet.yaml`, `file-watcher.yaml`.
5. Khởi động lại các Pod ứng dụng (`api-server`, `file-watcher`) để nhận thay đổi.
   Sau khi chạy xong, hãy đợi khoảng 30 giây để SQL Server khởi động hoàn tất trước khi sử dụng.

