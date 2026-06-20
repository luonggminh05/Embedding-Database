# 1. Cài đặt môi trường

## 1.1. Cần cài:

Trên Windows:

- VMware Workstation Pro hoặc VMware Workstation Player.
- File ISO Ubuntu Server 22.04 LTS hoặc 24.04 LTS.
- PowerShell/Windows Terminal.
- SSH client.
- kubectl, nếu muốn điều khiển Kubernetes từ Windows.
- SQL Server Management Studio hoặc VS Code kèm mssql extension, nếu cần kiểm tra SQL Server.

Trên máy ảo Ubuntu:

- OpenSSH server.
- RKE2 Kubernetes.
- kubectl của RKE2.
- Helm, nếu cần cài Rancher hoặc cert-manager.
- Docker/build tools theo yêu cầu của project, vì script deploy sẽ build image trực tiếp trên máy ảo.

Thông tin cần chuẩn bị:

- `<username>`: user đăng nhập Ubuntu.
- `<vm-ip>`: IP của máy ảo Ubuntu.
- Mật khẩu SQL Server `sa` đủ mạnh, dùng để tạo Kubernetes Secret.

---

## 1.2. Tạo máy ảo Ubuntu bằng VMware

### Bước 1: Cài VMware Workstation

1. Tải VMware Workstation Pro hoặc VMware Workstation Player từ trang chính thức của VMware/Broadcom.
2. Chạy file cài đặt `.exe` trên Windows và giữ các tùy chọn mặc định nếu không có yêu cầu đặc biệt.

### Bước 2: Tải Ubuntu Server ISO

Tải Ubuntu Server 22.04 LTS hoặc 24.04 LTS từ trang chính thức của Ubuntu. Nên dùng bản Server vì nhẹ hơn và phù hợp để chạy Kubernetes, SQL Server và model service.

### Bước 3: Tạo máy ảo

1. Mở VMware và chọn **Create a New Virtual Machine**.
2. Chọn **Installer disc image file (iso)** và trỏ tới file Ubuntu ISO vừa tải.
3. Nhập user và password cho Ubuntu.
4. Cấu hình tài nguyên khuyến nghị:
   - RAM: tối thiểu 8 GB, khuyến nghị 12 GB hoặc 16 GB.
   - CPU: tối thiểu 4 cores.
   - Disk: tối thiểu 50 GB, khuyến nghị 100 GB.
   - Network Adapter: chọn **Bridged** nếu muốn máy khác trong mạng truy cập được máy ảo, hoặc **NAT** nếu chỉ cần truy cập từ máy host.
5. Khi cài Ubuntu, chọn cài **OpenSSH server** để có thể SSH từ Windows.
6. Sau khi cài xong, đăng nhập Ubuntu và xem IP:

```bash
ip a
```

Ghi lại IP của máy ảo để thay cho `<vm-ip>` trong các lệnh bên dưới.

---

## 1.3. Kết nối SSH vào máy ảo

Từ PowerShell trên Windows:

```powershell
ssh <username>@<vm-ip>
```

Nếu kết nối thành công, các lệnh cài RKE2 và Helm bên dưới sẽ chạy trong terminal Ubuntu.

---

## 1.4. Cài RKE2 Kubernetes trên Ubuntu

Tải và cài RKE2 server:

```bash
curl -sfL https://get.rke2.io | sh -
```

Bật và khởi động service:

```bash
sudo systemctl enable rke2-server.service
sudo systemctl start rke2-server.service
```

Lần khởi động đầu có thể mất vài phút vì RKE2 cần tải container images.

Cấu hình `kubectl` cho user hiện tại:

```bash
mkdir -p ~/.kube
sudo cp /etc/rancher/rke2/rke2.yaml ~/.kube/config
sudo chown $(id -u):$(id -g) ~/.kube/config
echo 'export PATH=$PATH:/var/lib/rancher/rke2/bin' >> ~/.bashrc
source ~/.bashrc
```

Kiểm tra node:

```bash
kubectl get nodes
```

Node cần ở trạng thái `Ready`.

---

## 1.5. Cài đặt Docker trên Ubuntu

Script triển khai (`deploy_dotnet.ps1`) yêu cầu build Docker image trực tiếp trên máy ảo trước khi import vào RKE2. Do đó, bạn cần cài đặt Docker:

```bash
# Cài đặt các gói cần thiết
sudo apt-get update
sudo apt-get install -y ca-certificates curl

# Thêm khóa GPG chính thức của Docker
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
sudo chmod a+r /etc/apt/keyrings/docker.asc

# Thêm repository của Docker vào Apt sources
echo \
  "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu \
  $(. /etc/os-release && echo "$VERSION_CODENAME") stable" | \
  sudo tee /etc/apt/sources.list.d/docker.list > /dev/null
sudo apt-get update

# Cài đặt Docker
sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin

# Kiểm tra Docker đã chạy thành công chưa
sudo docker --version
```

---

## 1.6. Cài Helm và Rancher UI

Phần này cần để có giao diện web để quản lý Kubernetes

Cài Helm theo hướng dẫn chính thức của Helm, sau đó kiểm tra:

```bash
helm version
```

Cài cert-manager bằng Helm:

```bash
helm repo add jetstack https://charts.jetstack.io
helm repo update
helm install cert-manager jetstack/cert-manager \
  --namespace cert-manager \
  --create-namespace \
  --set crds.enabled=true
```

Cài Rancher bằng Helm:

```bash
helm repo add rancher-latest https://releases.rancher.com/server-charts/latest
helm repo update
kubectl create namespace cattle-system
helm install rancher rancher-latest/rancher \
  --namespace cattle-system \
  --set hostname=<rancher-hostname> \
  --set replicas=1 \
  --set bootstrapPassword=<bootstrap-password>
```

Thay `<rancher-hostname>` bằng domain nội bộ hoặc hostname cấu hình cho Rancher.

---

## 1.7. Cấu hình kubectl trên Windows tùy chọn

Nếu muốn chạy `kubectl` trực tiếp từ Windows:

1. Cài `kubectl` trên Windows.
2. Copy nội dung file `~/.kube/config` từ máy ảo Ubuntu.
3. Lưu vào:

```text
C:\Users\<User_Name>\.kube\config
```

4. Trong file config vừa copy, sửa dòng server từ IP nội bộ mặc định sang IP máy ảo:

```yaml
server: https://<vm-ip>:6443
```

5. Kiểm tra từ PowerShell:

```powershell
kubectl get nodes
```

---

## 1.8. Triển khai project

Có thể gom các lệnh build và upload image vào một script. Trước khi chạy script, cần kiểm tra các biến cấu hình sau:

- User và IP máy ảo phải đúng với môi trường hiện tại.
- Đường dẫn remote trên Ubuntu phải tồn tại hoặc script có bước tạo thư mục.
- Mật khẩu SQL Server `sa` phải đủ mạnh và được truyền qua biến môi trường hoặc nhập khi script hỏi.
- Các manifest Kubernetes trong thư mục `k8s` đã đúng image name, service name và NodePort mong muốn.

Sau khi deploy, kiểm tra các pod:

```bash
kubectl get pods
kubectl get svc
```

Nếu SQL Server vừa khởi động, nên đợi khoảng vài giây trước khi gọi API hoặc nạp tài liệu.

---
