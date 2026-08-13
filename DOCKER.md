# اجرای WaterMeterServer با Docker

## راه‌اندازی

```bash
cp .env.example .env
# مقادیر secrets را در .env تنظیم کنید
docker compose up -d --build
docker compose logs -f water-meter-server
```

سرویس TCP روی پورت `502` و API روی پورت `5080` در دسترس هستند. مقدار
`API_KEY` باید در هدر درخواست‌های API با نام `X-Api-Key` ارسال شود.

```bash
curl -H "X-Api-Key: YOUR_API_KEY" http://localhost:5080/api/meters
```

## فایل‌های Firmware

فایل‌های Firmware در volume دائمی `firmware-data` نگهداری می‌شوند. مسیر فایل
در رکورد `FirmwareUpgradeRequest` باید مسیر داخل کانتینر باشد:

```text
/var/lib/water-meter/firmware/my-firmware.bin
```

برای کپی فایل به volume:

```bash
docker compose cp ./my-firmware.bin water-meter-server:/var/lib/water-meter/firmware/
```

## توقف و پشتیبان‌گیری

```bash
docker compose down
docker compose exec postgres pg_dump -U watermeter watermeter > watermeter-backup.sql
```

برای حذف داده‌های دیتابیس و Firmware، باید به‌صورت صریح volumeها را حذف کنید:

```bash
docker compose down -v
```
