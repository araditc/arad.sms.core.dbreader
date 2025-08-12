namespace Arad.SMS.Core.DbReader;

public static class RuntimeSettings
{
    public static bool FullLog { get; set; }

    public static TimeSpan EndTime { get; set; }

    public static TimeSpan StartTime { get; set; }

    public static int TotalError { get; set; } = 10;

    public static int IntervalTime { get; set; } = 5;
    
    public static int MOIntervalTime { get; set; } = 60;

    public static int DLRIntervalTime { get; set; } = 60;

    public static int QueueCount { get; set; } = 5000;

    public static int BatchSize { get; set; }

    public static string ConnectionString { get; set; } = string.Empty;

    public static string DbProvider { get; set; } = "MySQL";

    public static bool EnableGetDlr { get; set; } = true;

    public static bool EnableGetMo { get; set; } = true;

    public static bool SendToWhiteList { get; set; }

    public static bool EnableSend { get; set; } = true;

    public static bool ReturnLongId { get; set; }

    public static int Tps { get; set; }

    public static bool UseApiKey { get; set; }

    public static int ApiVersion { get; set; }

    public static int Timeout { get; set; }

    public static string ApiKey { get; set; } = string.Empty;

    public static string DestinationAddress { get; set; } = string.Empty;

    public static string InsertQueryForInbox { get; set; } = string.Empty;

    public static string Password { get; set; } = string.Empty;

    public static string SelectQueryForGetDelivery { get; set; } = string.Empty;

    public static string SelectQueryForNullStatus { get; set; } = string.Empty;

    public static string SelectQueryForSend { get; set; } = string.Empty;

    public static string ServiceName { get; set; } = string.Empty;

    public static string SmsEndPointBaseAddress { get; set; } = string.Empty;

    public static string SourceAddress { get; set; } = string.Empty;

    public static string StatusForFailedSend { get; set; } = string.Empty;

    public static string StatusForSuccessSend { get; set; } = string.Empty;

    public static string UpdateQueryAfterFailedSend { get; set; } = string.Empty;

    public static string UpdateQueryAfterSend { get; set; } = string.Empty;

    public static string UpdateQueryBeforeSend { get; set; } = string.Empty;

    public static string UpdateQueryForDelivery { get; set; } = string.Empty;

    public static string UserName { get; set; } = string.Empty;

    public static string StatusForStored { get; set; } = string.Empty;

    public static string SelectQueryWhiteList { get; set; } = string.Empty;

    public static string InsertQueryForOutbox { get; set; } = string.Empty;

    public static string SelectDeliveryQuery { get; set; } = string.Empty;
    
    public static IConfiguration? Configuration { get; set; }

    public static bool ArchiveEnable { get; set; }

    public static TimeSpan ArchiveStartTime { get; set; }

    public static TimeSpan ArchiveEndTime { get; set; }

    public static int ArchiveBatchSize { get; set; }

    public static string OutboxTableName { get; set; } = string.Empty;

    public static string SelectQueryForArchive { get; set; } = string.Empty;

    public static string InsertQueryForArchive { get; set; } = string.Empty;

    public static string DeleteQueryAfterArchive { get; set; } = string.Empty;

    public static string DeleteQueryForDuplicateRecords { get; set; } = string.Empty;

    public static string SendApiKey { get; set; } = string.Empty;

    public static Task LoadSetting(CancellationToken cancellationToken)
    {
        if (Configuration == null)
        {
            return Task.CompletedTask;
        }

        ServiceName = Configuration["ServiceName"] ?? "";
        BatchSize = Convert.ToInt32(Configuration["Message:BatchSize"]);
        DLRIntervalTime = Convert.ToInt32(Configuration["Message:DLRInterval"]);
        MOIntervalTime = Convert.ToInt32(Configuration["Message:MOInterval"]);
        Tps = Convert.ToInt32(Configuration["Message:TPS"]);
        EnableSend = Convert.ToBoolean(Configuration["Message:EnableSend"]);
        SendToWhiteList = Convert.ToBoolean(Configuration["Message:SendToWhiteList"]);
        EnableGetDlr = Convert.ToBoolean(Configuration["Message:EnableGetDLR"]);
        EnableGetMo = Convert.ToBoolean(Configuration["Message:EnableGetMO"]);
        DbProvider = Configuration["DB:Provider"] ?? "";
        ConnectionString = Configuration["DB:ConnectionString"] ?? "";
        SelectQueryForSend = Configuration["DB:SelectQueryForSend"] ?? "";
        UpdateQueryAfterSend = Configuration["DB:UpdateQueryAfterSend"] ?? "";
        UpdateQueryBeforeSend = Configuration["DB:UpdateQueryBeforeSend"] ?? "";
        StatusForSuccessSend = Configuration["DB:StatusForSuccessSend"] ?? "";
        UpdateQueryAfterFailedSend = Configuration["DB:UpdateQueryAfterFailedSend"] ?? "";
        StatusForFailedSend = Configuration["DB:StatusForFailedSend"] ?? "";
        StatusForStored = Configuration["DB:StatusForStore"] ?? "";
        SelectQueryForGetDelivery = Configuration["DB:SelectQueryForGetDelivery"] ?? "";
        UpdateQueryForDelivery = Configuration["DB:UpdateQueryForDelivery"] ?? "";
        InsertQueryForInbox = Configuration["DB:InsertQueryForInbox"] ?? "";
        SelectQueryForNullStatus = Configuration["DB:SelectQueryForNullStatus"] ?? "";
        SelectQueryForArchive = Configuration["DB:SelectQueryForArchive"] ?? "";
        InsertQueryForArchive = Configuration["DB:InsertQueryForArchive"] ?? "";
        DeleteQueryAfterArchive = Configuration["DB:DeleteQueryAfterArchive"] ?? "";
        DeleteQueryForDuplicateRecords = Configuration["DB:DeleteQueryForDuplicateRecords"] ?? "";
        SelectQueryWhiteList = Configuration["DB:SelectQueryWhiteList"] ?? "";
        OutboxTableName = Configuration["DB:OutboxTableName"] ?? "";
        InsertQueryForOutbox = Configuration["DB:InsertQueryForOutbox"] ?? "";
        SelectDeliveryQuery = Configuration["DB:SelectDeliveryQuery"] ?? "";
        SendApiKey = Configuration["URLSetting:SendApiKey"] ?? "";

        #region API Setting
        UserName = Configuration["SmsEndPointConfig:UserName"] ?? "";
        Password = Configuration["SmsEndPointConfig:Password"] ?? "";
        SmsEndPointBaseAddress = Configuration["SmsEndPointConfig:SmsEndPointBaseAddress"] ?? "";
        UseApiKey = Convert.ToBoolean(Configuration["SmsEndPointConfig:UseApiKey"]);
        ApiKey = Configuration["SmsEndPointConfig:ApiKey"] ?? "";
        ReturnLongId = Convert.ToBoolean(Configuration["SmsEndPointConfig:ReturnLongId"]);
        ApiVersion = Convert.ToInt32(Configuration["SmsEndPointConfig:ApiVersion"]);
        Timeout = Convert.ToInt32(Configuration["SmsEndPointConfig:Timeout"]);
        #endregion

        #region Bulk time setting
        StartTime = TimeSpan.Parse(Configuration["BulkTimeSettings:Start"] ?? string.Empty);
        EndTime = TimeSpan.Parse(Configuration["BulkTimeSettings:End"] ?? string.Empty);
        #endregion

        #region Allert setting
        SourceAddress = Configuration["AlertSetting:SourceAddress"] ?? "";
        DestinationAddress = Configuration["AlertSetting:DestinationAddress"] ?? "";
        TotalError = Convert.ToInt32(Configuration["AlertSetting:ErrorCount"]);
        IntervalTime = Convert.ToInt32(Configuration["AlertSetting:IntervalTime"]);
        QueueCount = Convert.ToInt32(Configuration["AlertSetting:QueueCount"]);
        #endregion

        #region Archive time setting
        ArchiveEnable = Convert.ToBoolean(Configuration["ArchiveTimeSettings:EnableArchive"]);
        ArchiveStartTime = TimeSpan.Parse(Configuration["ArchiveTimeSettings:Start"]!);
        ArchiveEndTime = TimeSpan.Parse(Configuration["ArchiveTimeSettings:End"]!);
        ArchiveBatchSize = Convert.ToInt32(Configuration["ArchiveTimeSettings:BatchSize"]);
        #endregion

        FullLog = Convert.ToBoolean(Configuration["FullLog"]);
        return Task.CompletedTask;
    }
}
