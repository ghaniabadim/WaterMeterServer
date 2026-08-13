# استقرار Water Meter Server روی Linux

## انتشار

روی سیستم توسعه:

```bash
dotnet publish WaterMeterServer.WorkerService/WorkerService.csproj \
  -c Release -r linux-x64 --self-contained false \
  -o .deploy/linux-publish
```

محتویات `.deploy/linux-publish` را در سرور به `/opt/water-meter-server` منتقل کنید.

## ساخت کاربر و نصب سرویس

```bash
sudo useradd --system --no-create-home --shell /usr/sbin/nologin watermeter
sudo mkdir -p /opt/water-meter-server
sudo chown -R watermeter:watermeter /opt/water-meter-server
sudo cp water-meter-server.service /etc/systemd/system/water-meter-server.service
```

## تنظیم secrets

فایل `/etc/water-meter-server.env` را بسازید:

```ini
ConnectionStrings__DefaultConnection=Host=127.0.0.1;Port=5432;Database=watermeter;Username=postgres;Password=CHANGE_ME
Protocol__AesKey=CHANGE_ME_32_CHARACTER_AES_KEY
Api__ApiKey=CHANGE_ME_LONG_RANDOM_API_KEY
Api__Listen=http://0.0.0.0
Api__Port=5080
TcpServer__Port=502
```

سپس:

```bash
sudo chmod 600 /etc/water-meter-server.env
sudo chown root:root /etc/water-meter-server.env
```

## فعال‌سازی و بررسی

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now water-meter-server
sudo systemctl status water-meter-server
sudo journalctl -u water-meter-server -f
```

## تست API

```bash
curl -H "X-Api-Key: CHANGE_ME_LONG_RANDOM_API_KEY" \
  http://127.0.0.1:5080/api/meters
```

برای دسترسی از شبکه، پورت‌های TCP `502` و API `5080` را فقط در فایروال و Security Group موردنیاز باز کنید.
