👉 [نسخه پارسی](https://github.com/araditc/arad.sms.core.dbreader/blob/master/README_FA.md)

# Arad SMS Core DBReader

## Project Overview

`arad.sms.core.dbreader` is a high-performance message ingestion and dispatch service designed to interface with Oracle, SQL Server, and MySQL databases. It reads outbound SMS messages from a database, delivers them via Arad SMS Web Service, and stores inbound messages and delivery receipts using API calls or Webhooks.

It is an essential module in Arad's SMS infrastructure, built with multithreaded architecture for maximum throughput and reliability.

---

## Key Features

- **Database Connectivity**:
  - Supports Oracle, SQL Server, and MySQL
  - Reads from two key tables:
    - `Outbound`: Stores messages to be sent
    - `Inbound`: Stores received messages

- **Message Dispatch**:
  - Reads messages from the `Outbound` table
  - Sends messages via Arad SMS Web API

- **Delivery Report Handling**:
  - Supports two modes:
    - API-based `GetDLR` polling
    - Webhook-based delivery status push

- **Inbound Message Reception**:
  - Pull via API (`GetMO`) or accept via Webhook
  - Stores messages in the `Inbound` table

- **Internal API**:
  - Accepts user messages and queues them for dispatch via Arad Web Service

- **Error Management**:
  - Compliant with Arad’s official standards  
    👉 [API Docs](https://github.com/araditc/arad.sms.core.api.document)

- **Archiving & Cleanup**:
  - Automatically archives old messages
  - Removes duplicate or obsolete records

---

## Architecture & Technical Highlights

- **Multithreaded Design**:
  Ensures concurrent processing of high-volume message traffic.

- **Configuration-Driven**:
  Uses `appsettings.json` for all runtime configuration—databases, APIs, archiving, logging, error alerts, and more.

- **Protocol Support**:
  Fully aligned with Arad Web API standards, including Swagger-documented internal API endpoints.

---

## Configuration – `appsettings.json`

A few important configuration blocks:

- **LogConfig**: Logging location, file rotation, and retention
- **DB**: All SQL queries used for send, receive, update, archive
- **Message**: TPS control, batching, delivery polling intervals
- **SmsEndPointConfig**: Arad API authentication and versioning
- **ArchiveTimeSettings**: Archive scheduler (start/end time and batch size)
- **AlertSetting**: Threshold-based alert messaging
- **URLSetting**: Internal API hosting (IP, port, API key)

👉 See full `appsettings.json` structure in the project for details.

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

---

## Installation

### Prerequisites

- Oracle / SQL Server / MySQL database
- Valid Arad SMS Web Service credentials (username, password, API key)
- .NET runtime environment (tested on .NET Core 8+)

---

## Setup & Run

```bash
git clone https://github.com/araditc/arad.sms.core.dbreader
cd arad.sms.core.dbreader
dotnet restore
dotnet run
```

---

## Deploying as a Windows Service

1. Publish the project via Visual Studio
2. Install the service using:

```bash
sc create DbProxy_Reader binpath="C:\Path\To\Published\dbreader.exe"
```

3. Manage service:

```bash
sc start DbProxy_Reader
sc stop DbProxy_Reader
sc delete DbProxy_Reader
```

*Note: Run these in an Administrator Command Prompt.*

---

## Deploying on Linux (systemd)

1. Publish project:

```bash
dotnet publish -c Release -o /path/to/publish
```

2. Create systemd service:

```bash
sudo nano /etc/systemd/system/dbproxy_reader.service
```

3. Add content:

```ini
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

4. Start service:

```bash
sudo systemctl enable dbproxy_reader.service
sudo systemctl start dbproxy_reader.service
```

5. Manage:

```bash
sudo systemctl status dbproxy_reader.service
sudo systemctl stop dbproxy_reader.service
sudo systemctl disable dbproxy_reader.service
```

---

## Troubleshooting

- **Database Connection**:
  - Verify `ConnectionString` and `Provider` in `appsettings.json`
- **API Auth Failure**:
  - Check `SmsEndPointBaseAddress`, `UserName`, `Password`, and `ApiKey`
- **No Delivery or Inbound**:
  - Confirm Webhook endpoints are accessible
- **Debug Logs**:
  - Located at `LogConfig.LogFileAddressDirectory`

---

## Documentation

- [Arad SMS Core API Documentation](https://github.com/araditc/arad.sms.core.api.document)
- Swagger support for internal API

---

## License

© Arad ITC. All rights reserved.
