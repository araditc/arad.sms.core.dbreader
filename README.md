# مستندات پروژه arad.sms.core.dbreader

## معرفی پروژه
پروژه `arad.sms.core.dbreader` یک سرویس برای مدیریت پیام‌های ورودی (Inbound) و خروجی (Outbound) است که با اتصال به دیتابیس‌های Oracle، SQL Server و MySQL کار می‌کند. این سرویس پیام‌ها را از جدول Outbound خوانده و از طریق وب‌سرویس شرکت آراد رایانه لیان ارسال می‌کند. همچنین، قابلیت دریافت پیام‌ها و به‌روزرسانی وضعیت دلیوری را از طریق وب‌سرویس یا Webhook فراهم می‌کند. پروژه از Threading برای مدیریت حجم بالای پیام‌ها استفاده می‌کند و شامل یک API داخلی برای دریافت پیام‌ها از کاربران است.

## قابلیت‌ها
- **اتصال به دیتابیس**: پشتیبانی از Oracle، SQL Server و MySQL با دو جدول:
  - **Inbound**: ذخیره پیام‌های دریافتی.
  - **Outbound**: ذخیره پیام‌هایی که باید ارسال شوند.
- **ارسال پیام‌ها**: خواندن پیام‌ها از جدول Outbound و ارسال به وب‌سرویس آراد.
- **به‌روزرسانی وضعیت دلیوری**:
  - فراخوانی وب‌سرویس GetDLR.
  - دریافت دلیوری از طریق Webhook.
- **دریافت پیام‌ها**: ذخیره پیام‌های دریافتی در جدول Inbound از دو روش:
  - فراخوانی وب‌سرویس GetMO.
  - دریافت پیام‌ها از طریق Webhook.
- **API داخلی**: دریافت پیام‌ها از کاربران و ذخیره در جدول Outbound برای ارسال به وب‌سرویس آراد.
- **مدیریت خطاها**: مطابق با استانداردهای آراد ([مستندات](https://github.com/araditc/arad.sms.core.api.document)).
- **آرشیو و حذف پیام‌ها**: پشتیبانی از آرشیو پیام‌های قدیمی و حذف رکوردهای تکراری.

## جزئیات فنی
- **معماری**: استفاده از Threading برای پردازش هم‌زمان پیام‌ها.
- **مدیریت خطاها**: پیاده‌سازی استانداردهای آراد برای مدیریت خطاها.
- **پروتکل ارتباطی**: وب‌سرویس آراد از پروتکل‌های مشخص‌شده در [مستندات](https://github.com/araditc/arad.sms.core.api.document).
- **مستندات API**: API داخلی با Swagger مستندسازی شده است.

## تنظیمات (appsettings.json)
فایل `appsettings.json` هسته اصلی پیکربندی پروژه است و شامل تنظیمات مربوط به دیتابیس، وب‌سرویس آراد، لاگ‌گیری، ارسال پیام‌ها، آرشیو و هشدارها می‌باشد. در ادامه، ساختار فایل و توضیحات هر بخش ارائه شده است:

### نمونه فایل appsettings.json
```json
{
  "LogConfig": {
    "LogFileAddressDirectory": "C:\\Arad.SMS.Core\\Logs\\ProxyDB\\",
    "LogFileName": "Proxy.Log",
    "FileSizeLimit": 10000000,
    "RetainedFileCountLimit": 1500
  },
  "DB": {
    "Provider": "MySQL",
    "ConnectionString": "Server=localhost;Database=test;Uid=root;Pwd=12346;",
    "SelectQueryForSend": "SELECT outboundmessage_id as ID, from_mobile_number as SOURCEADDRESS, dest_mobile_number as DESTINATIONADDRESS, message_body as MESSAGETEXT from outbound_messages where status is null LIMIT {0}",
    "UpdateQueryBeforeSend": "UPDATE outbound_messages set status = 'WaitingForSend' where outboundmessage_id in ({0});",
    "UpdateQueryAfterSend": "UPDATE outbound_messages set status = '{0}', sent_time = '{1}', ticket = '{2}', selection_id = {3}, UpstramGateway = {4} where outboundmessage_id = {5};",
    "StatusForSuccessSend": "Sent",
    "UpdateQueryAfterFailedSend": "UPDATE outbound_messages set status = '{0}' where outboundmessage_id = {1};",
    "StatusForFailedSend": "ErrorInSending",
    "StatusForStore": "Stored",
    "SelectQueryForGetDelivery": "select ticket from outbound_messages where status = 'Sent' and sent_time > date_sub(now(), INTERVAL 6 hour) LIMIT 900 offset {0}",
    "UpdateQueryForDelivery": "UPDATE outbound_messages set status = '{0}', update_time = '{1}' where ticket = '{2}';",
    "InsertQueryForInbox": "INSERT INTO inbound_messages (source_number, mobile_number, date, message) VALUES ('{0}','{1}','{2}','{3}');",
    "SelectQueryForNullStatus": "SELECT COUNT(*) as count from outbound_messages where status is null;",
    "EnableCopyFromOutgoingToOutbound": false,
    "SelectQueryForOutgoing": "Select id as ID, from_mobile_number as SOURCEADDRESS, dest_mobile_number as DESTINATIONADDRESS, message_body as MESSAGETEXT from outgoing_message where status is null and Not EXISTS(SELECT message_id FROM outbound_messages WHERE message_id = id) LIMIT {0}",
    "InsertQueryForOutgoing": "INSERT outbound_messages (from_mobile_number, dest_mobile_number, message_body, creation_date, message_id) VALUES ('{0}','{1}','{2}', '{3}', {4});",
    "UpdateQueryForOutgoing": "UPDATE outgoing_message set status = 4 where id in ({0});",
    "SelectQueryForArchive": "SELECT outboundmessage_id as ID, creation_date as CreationDate FROM outbound_messages where DATE(creation_date) <= DATE(DATE_SUB(SYSDATE(), INTERVAL 4 DAY)) limit {0};",
    "OutboxTableName": "outbound_messages",
    "InsertQueryForArchive": "INSERT INTO {0} SELECT * FROM outbound_messages where outboundmessage_id in ({1});",
    "DeleteQueryAfterArchive": "DELETE FROM outbound_messages WHERE outboundmessage_id IN ({0})",
    "DeleteQueryForDuplicateRecords": "DELETE from {0} where outboundmessage_id IN (SELECT outboundmessage_id FROM outbound_messages where DATE(creation_date) <= DATE(DATE_SUB(SYSDATE(), INTERVAL 4 DAY)));",
    "SelectQueryWhiteList": "select mobile from whitelist where mobile in ({0})",
    "InsertQueryForOutbox": "INSERT outbound_messages (from_mobile_number, dest_mobile_number, message_body, creation_date, message_id) VALUES ('{0}','{1}','{2}', '{3}', {4});",
    "SelectDeliveryQuery": "select status from outbound_messages where message_id in ({0});"
  },
  "FullLog": false,
  "Message": {
    "TPS": 1000,
    "BatchSize": 500,
    "EnableSend": true,
    "EnableGetDLR": true,
    "EnableGetMO": true,
    "DLRInterval": 60,
    "MOInterval": 60,
    "SendToWhiteList": false
  },
  "ServiceName": "DbProxy_Reader",
  "SmsEndPointConfig": {
    "SmsEndPointBaseAddress": "http://localhost:50004",
    "UserName": "user",
    "Password": "pass",
    "UseApiKey": true,
    "ApiKey": "+u",
    "ReturnLongId": false,
    "ApiVersion": 4
  },
  "BulkTimeSettings": {
    "Start": "00:00:00",
    "End": "23:59:59"
  },
  "ArchiveTimeSettings": {
    "EnableArchive": true,
    "Start": "00:00:00",
    "End": "04:00:00",
    "BatchSize": 5000
  },
  "AlertSetting": {
    "SourceAddress": "98xxx",
    "DestinationAddress": "989xxxxxx",
    "ErrorCount": "100",
    "IntervalTime": "10",
    "QueueCount": "5000"
  },
  "URLSetting": {
    "IP": "127.0.0.1",
    "Port": "8888",
    "SendApiKey": "123456"
  }
}
```

## توضیحات تنظیمات
### LogConfig

- LogFileAddressDirectory: مسیر ذخیره فایل‌های لاگ (مثال: C:\\Arad.SMS.Core\\Logs\\ProxyDB\\).
- LogFileName: نام فایل لاگ (مثال: Proxy.Log).
- FileSizeLimit: حداکثر اندازه فایل لاگ (به بایت، مثال: 10MB).
- RetainedFileCountLimit: حداکثر تعداد فایل‌های لاگ که نگهداری می‌شوند (مثال: 1500).

### DB

- Provider: نوع دیتابیس (MySQL، SQL، Oracle).
- ConnectionString: رشته اتصال به دیتابیس (شامل سرور، نام دیتابیس، نام کاربری و رمز عبور).
- SelectQueryForSend: کوئری برای انتخاب پیام‌های آماده ارسال از جدول Outbound.
- UpdateQueryBeforeSend: به‌روزرسانی وضعیت پیام‌ها به WaitingForSend قبل از ارسال.
- UpdateQueryAfterSend: به‌روزرسانی وضعیت، زمان ارسال، تیکت و اطلاعات دیگر پس از ارسال موفق.
- StatusForSuccessSend: وضعیت پیام پس از ارسال موفق (مثال: Sent).
- UpdateQueryAfterFailedSend: به‌روزرسانی وضعیت پیام در صورت شکست ارسال.
- StatusForFailedSend: وضعیت پیام در صورت شکست (مثال: ErrorInSending).
- StatusForStore: وضعیت پیام‌های ذخیره‌شده (مثال: Stored).
- SelectQueryForGetDelivery: کوئری برای انتخاب تیکت‌های پیام‌های ارسالی برای دریافت دلیوری.
- UpdateQueryForDelivery: به‌روزرسانی وضعیت دلیوری پیام‌ها.
- InsertQueryForInbox: درج پیام‌های دریافتی در.Table
- SelectQueryForNullStatus: شمارش پیام‌های بدون وضعیت.
- EnableCopyFromOutgoingToOutbound: فعال‌سازی کپی پیام‌ها از جدول Outgoing به Outbound.
- SelectQueryForOutgoing: انتخاب پیام‌ها از جدول Outgoing برای انتقال به Outbound.
- InsertQueryForOutgoing: درج پیام‌ها در جدول Outbound.
- UpdateQueryForOutgoing: به‌روزرسانی وضعیت پیام‌ها در جدول Outgoing.
- SelectQueryForArchive: انتخاب پیام‌های قدیمی برای آرشیو.
- OutboxTableName: نام جدول Outbound.
- InsertQueryForArchive: درج پیام‌ها در جدول آرشیو.
- DeleteQueryAfterArchive: حذف پیام‌ها از Outbound پس از آرشیو.
- DeleteQueryForDuplicateRecords: حذف رکوردهای تکراری قدیمی.
- SelectQueryWhiteList: بررسی شماره‌های موجود در لیست سفید.
- InsertQueryForOutbox: درج پیام‌ها در جدول Outbound.
- SelectDeliveryQuery: بررسی وضعیت دلیوری پیام‌ها.

### FullLog

- فعال‌سازی لاگ‌گیری کامل (true یا false).

### Message

- TPS: تعداد پیام در ثانیه (Transactions Per Second).
- BatchSize: تعداد پیام‌ها در هر دسته برای پردازش.
- EnableSend: فعال‌سازی ارسال پیام‌ها.
- EnableGetDLR: فعال‌سازی دریافت دلیوری.
- EnableGetMO: فعال‌سازی دریافت پیام‌های ورودی.
- DLRInterval: فاصله زمانی برای دریافت دلیوری (به دقیقه).
- MOInterval: فاصله زمانی برای دریافت پیام‌های ورودی (به دقیقه).
- SendToWhiteList: محدود کردن ارسال به شماره‌های موجود در لیست سفید.

### ServiceName

- نام سرویس (مثال: DbProxy_Reader).

### SmsEndPointConfig

- SmsEndPointBaseAddress: آدرس پایه وب‌سرویس آراد.
- UserName: نام کاربری برای وب‌سرویس.
- Password: رمز عبور برای وب‌سرویس.
- UseApiKey: استفاده از کلید API.
- ApiKey: کلید API برای احراز هویت.
- ReturnLongId: بازگشت شناسه بلند (true یا false).
- ApiVersion: نسخه API وب‌سرویس.

### BulkTimeSettings

- Start: زمان شروع ارسال انبوه.
- End: زمان پایان ارسال انبوه.

### ArchiveTimeSettings

- EnableArchive: فعال‌سازی آرشیو پیام‌ها.
- Start: زمان شروع آرشیو.
- End: زمان پایان آرشیو.
- BatchSize: تعداد پیام‌ها در هر دسته برای آرشیو.

### AlertSetting

- SourceAddress: شماره مبدأ برای ارسال هشدار.
- DestinationAddress: شماره مقصد برای ارسال هشدار.
- ErrorCount: تعداد خطاها برای فعال‌سازی هشدار.
- IntervalTime: فاصله زمانی بین هشدارها (به دقیقه).
- QueueCount: حداکثر تعداد پیام‌ها در صف برای ارسال هشدار.

### URLSetting

- IP: آدرس IP برای API داخلی.
- Port: پورت API داخلی.
- SendApiKey: کلید API برای ارسال پیام‌ها از طریق API داخلی.


### نکته: برای جزئیات وب‌سرویس آراد، به مستندات مراجعه کنید.

## نصب و راه‌اندازی

### پیش‌نیازها:

- دیتابیس (Oracle، SQL Server یا MySQL).
- دسترسی به وب‌سرویس آراد با اطلاعات معتبر (نام کاربری، رمز عبور، API Key).
- محیط اجرایی .NET.


### پیکربندی:

- فایل appsettings.json را با اطلاعات دیتابیس، وب‌سرویس آراد و تنظیمات دیگر پر کنید.
- اطمینان حاصل کنید که مسیر لاگ‌گیری و Webhook (در صورت استفاده) در دسترس هستند.


### اجرا:
```bash
git clone https://github.com/araditc/arad.sms.core.dbreader
dotnet restore
dotnet run
```


### بررسی لاگ‌ها:

لاگ‌ها در مسیر مشخص‌شده در LogConfig ذخیره می‌شوند.



## نصب به عنوان سرویس
### ویندوز
برای نصب سرویس روی ویندوز، ابتدا پروژه را با Visual Studio بدون خطا Publish کنید. سپس از دستور زیر برای نصب سرویس استفاده کنید:
```bash
sc create DbProxy_Reader binpath="C:\Path\To\Published\dbreader.exe"
```

- DbProxy_Reader: نام سرویس (با مقدار ServiceName در appsettings.json مطابقت داشته باشد).
- binpath: مسیر فایل اجرایی پروژه پس از Publish.

#### برای شروع سرویس:
```bash
sc start DbProxy_Reader
```

#### برای توقف سرویس:
```bash
sc stop DbProxy_Reader
```

#### برای حذف سرویس:
```bash
sc delete DbProxy_Reader
```

**نکته: دستورات باید در Command Prompt با دسترسی Administrator اجرا شوند.**

### لینوکس
برای نصب سرویس روی لینوکس (مثلاً Ubuntu)، پروژه را با dotnet publish منتشر کنید و از systemd برای مدیریت سرویس استفاده کنید:

فایل اجرایی پروژه را منتشر کنید:
```bash
dotnet publish -c Release -o /path/to/publish
```


یک فایل سرویس برای systemd ایجاد کنید:
```bash
sudo nano /etc/systemd/system/dbproxy_reader.service
```


محتوای زیر را در فایل سرویس قرار دهید:
```bash
[Unit]
Description=Arad SMS Core DB Reader Service
After=network.target

[Service]
ExecStart=/path/to/publish/dbreader --urls "http://0.0.0.0:8888"
WorkingDirectory=/path/to/publish
Restart=always
User=your_user
Environment=ASPNETCORE_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
```

- ExecStart: مسیر فایل اجرایی و پورت (مطابق با URLSetting.Port در appsettings.json).
- WorkingDirectory: مسیر دایرکتوری فایل‌های منتشرشده.
- User: کاربر لینوکس که سرویس با آن اجرا می‌شود.


سرویس را فعال و شروع کنید:
```bash
sudo systemctl enable dbproxy_reader.service
sudo systemctl start dbproxy_reader.service
```


برای بررسی وضعیت سرویس:
```bash
sudo systemctl status dbproxy_reader.service
```


برای توقف یا حذف سرویس:
```bash
sudo systemctl stop dbproxy_reader.service
sudo systemctl disable dbproxy_reader.service
```


#### نکته: اطمینان حاصل کنید که مسیرهای فایل و پورت‌ها با تنظیمات پروژه مطابقت دارند.

## نکات عیب‌یابی
- اتصال به دیتابیس: بررسی کنید که ConnectionString و Provider درست تنظیم شده باشند.
- وب‌سرویس آراد: مطمئن شوید که SmsEndPointBaseAddress، UserName، Password و ApiKey معتبر هستند.
- Webhook: سرور Webhook باید فعال و در دسترس باشد.
- لاگ‌ها: فایل‌های لاگ را در مسیر مشخص‌شده در LogConfig بررسی کنید.



