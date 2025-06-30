//
//  --------------------------------------------------------------------
//  Copyright (c) 2005-2024 Arad ITC.
//
//  Author : Ammar Heidari <ammar@arad-itc.org>
//  Licensed under the Apache License, Version 2.0 (the "License")
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0 
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
//  --------------------------------------------------------------------

using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.RegularExpressions;

using Arad.SMS.Core.DbReader.Models;

using Flurl;

using Microsoft.Data.SqlClient;

using MySql.Data.MySqlClient;

using Newtonsoft.Json;

using Oracle.ManagedDataAccess.Client;

using Serilog;

using JsonSerializer = System.Text.Json.JsonSerializer;

namespace Arad.SMS.Core.DbReader.Services;

public class Worker(IHttpClientFactory clientFactory) : BackgroundService
{
    private string _accessToken = "";
    private static int _errorCount;
    private static string _messageText = string.Empty;
    public CancellationTokenSource CancellationTokenSource { get; set; } = new();
    public List<ScheduledJob> ScheduledJobs { get; set; } = [];
    public List<Task> JobTasks { get; set; } = [];
    private bool _disposed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)  
    {
        try
        {
            Log.Information("Execute service");

            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, CancellationTokenSource.Token);
            CancellationToken cancellationToken = linkedCts.Token;

            await RuntimeSettings.LoadSetting(cancellationToken);
            
            if (!RuntimeSettings.UseApiKey)
            {
                GetToken(cancellationToken);
            }
            
            await GetMoAsync(cancellationToken);
            await GetDeliveryAsync(cancellationToken);
            await CalculateNullStatusAsync(cancellationToken);
            
            ScheduledJobs.Add(new ("Send", TimeSpan.FromSeconds(1), ReadAndPostAsync));
            ScheduledJobs.Add(new ("ArchiveData", TimeSpan.FromSeconds(1), ArchiveDataAsync));
            ScheduledJobs.Add(new ("CopyFromOutgoingToOutbound", TimeSpan.FromSeconds(1), CopyFromOutgoingToOutboundAsync));
            ScheduledJobs.Add(new ("CreateAlertMessage", TimeSpan.FromSeconds(1), CreateAlertMessageAsync));

            if (RuntimeSettings.MOIntervalTime > 0)
            {
                ScheduledJobs.Add(new("GetMo", TimeSpan.FromMinutes(RuntimeSettings.MOIntervalTime), GetMoAsync));
            }

            if (RuntimeSettings.DLRIntervalTime > 0)
            {
                ScheduledJobs.Add(new("GetDelivery", TimeSpan.FromMinutes(RuntimeSettings.DLRIntervalTime), GetDeliveryAsync));
            }

            if (RuntimeSettings.IntervalTime > 0)
            {
                ScheduledJobs.Add(new("CalculateNullStatus", TimeSpan.FromMinutes(RuntimeSettings.IntervalTime), CalculateNullStatusAsync));
            }
            
            foreach (Task jobTask in ScheduledJobs.Select(job => RunJobLoop(job, cancellationToken)))
            {
                JobTasks.Add(jobTask);
            }

            await Task.WhenAll(JobTasks);
        }
        catch (Exception e)
        {
            Log.Error(e.Message);
        }
    }
    
    private async Task RunJobLoop(ScheduledJob job, CancellationToken token)
    {
        Log.Information($"Started job : {job.Name}");

        while (!token.IsCancellationRequested)
        {
            int failureCount = 0;
            const int maxFailures = 5;

            try
            {
                if (job is { UseAlignment: true, AlignmentStartTime: not null })
                {
                    DateTime now = DateTime.Now;
                    DateTime today = now.Date + job.AlignmentStartTime.Value;

                    DateTime nextRunTime;
                    if (now < today)
                    {
                        nextRunTime = today;
                    }
                    else
                    {
                        int intervalsPassed = (int)((now - today).TotalMilliseconds / job.Interval.TotalMilliseconds) + 1;
                        nextRunTime = today.AddMilliseconds(intervalsPassed * job.Interval.TotalMilliseconds);
                    }

                    TimeSpan delay = nextRunTime - now;
                    Log.Information($"Delaying {job.Name} until aligned time: {nextRunTime}");
                    await Task.Delay(delay, token);
                }
                
                PeriodicTimer timer = new (job.Interval);
                
                while (await timer.WaitForNextTickAsync(token))
                {
                    //Log.Information($"Running {job.Name} at {DateTimeOffset.Now}");
                    await job.Action(token);

                    failureCount = 0;
                }
            }
            catch (OperationCanceledException)
            {
                Log.Error($"{job.Name} canceled.");
                break;
            }
            catch (Exception ex)
            {
                failureCount++;
                Log.Information(ex, $"Error in job {job.Name} (failure #{failureCount})");

                if (failureCount >= maxFailures)
                {
                    failureCount = 0;//TODO
                    Log.Error($"Job {job.Name} failed {failureCount} times. Triggering fallback/alerting and stopping.");
                    //await TriggerJobAlertAsync(job, ex, token);
                    
                    //break;
                }
            }
        }
        
        Log.Information($"Stopped job : {job.Name}");
    }

    private async void GetToken(CancellationToken token)
    {
        try
        {
            Log.Information("Start getting token");
            HttpClient client = clientFactory.CreateClient();

            HttpRequestMessage request = new(HttpMethod.Post, $"{RuntimeSettings.SmsEndPointBaseAddress}/connect/token");
            MultipartFormDataContent multipartContent = new();
            multipartContent.Add(new StringContent(RuntimeSettings.UserName, Encoding.UTF8, MediaTypeNames.Text.Plain), "username");
            multipartContent.Add(new StringContent(RuntimeSettings.Password, Encoding.UTF8, MediaTypeNames.Text.Plain), "password");

            request.Content = multipartContent;
            HttpResponseMessage response = await client.SendAsync(request, token);
            response.EnsureSuccessStatusCode();

            TokenResponseModel? data = await response.Content.ReadFromJsonAsync<TokenResponseModel>(cancellationToken: token);
            _accessToken = data?.AccessToken!;
            Log.Information($"Token: {_accessToken}");
        }
        catch (Exception e)
        {
            Log.Error($"Error in getting token: {e.Message}");
        }
    }
    
    private async Task ArchiveDataAsync(CancellationToken token)
    {
        if (!(RuntimeSettings.ArchiveEnable && DateTime.Now.TimeOfDay > RuntimeSettings.ArchiveStartTime && DateTime.Now.TimeOfDay < RuntimeSettings.ArchiveEndTime))
        {
            return;
        }

        DataTable dt = new();
        string archiveTableName = $"{RuntimeSettings.OutboxTableName}_Archive"; //default name

        try
        {
            Stopwatch sw1 = new();
            Stopwatch sw2 = new();
            Stopwatch sw3 = new();
            Stopwatch sw4 = new();

            await using MySqlConnection cn = new(RuntimeSettings.ConnectionString);

            await cn.OpenAsync(token);
            sw1.Start();

            await using (MySqlCommand cm = new(string.Format(RuntimeSettings.SelectQueryForArchive, RuntimeSettings.ArchiveBatchSize), cn))
            {
                dt.Load(cm.ExecuteReader());
            }

            sw1.Stop();
            await cn.CloseAsync();

            List<string> archiveIds = [];

            if (dt.Rows.Count > 0)
            {
                sw2.Start();

                DateTime? creationDate = dt.Rows[0].Field<DateTime>("CreationDate");
                if (creationDate != null)
                {
                    archiveTableName = await CreateArchiveTable(creationDate.Value, token);
                }

                archiveIds.AddRange(from DataRow dr in dt.Rows select dr["ID"].ToString()!);

                sw2.Stop();

                if (archiveIds.Any())
                {
                    sw3.Start();
                    string ids = string.Join(",", archiveIds);
                    await using MySqlConnection con = new(RuntimeSettings.ConnectionString);
                    await con.OpenAsync(token);
                    string command = string.Format(RuntimeSettings.InsertQueryForArchive, archiveTableName, ids);
                    await using MySqlCommand cm = new(command, con) { CommandType = CommandType.Text };
                    await cm.ExecuteNonQueryAsync(token);
                    await cm.Connection.CloseAsync();
                    await con.CloseAsync();
                    sw3.Stop();
                    sw4.Start();

                    await using MySqlConnection conn = new(RuntimeSettings.ConnectionString);
                    await conn.OpenAsync(token);
                    string deleteCommand = string.Format(RuntimeSettings.DeleteQueryAfterArchive, ids);
                    await using MySqlCommand dlCommand = new(deleteCommand, conn) { CommandType = CommandType.Text };
                    await dlCommand.ExecuteNonQueryAsync(token);
                    await dlCommand.Connection.CloseAsync();
                    sw4.Stop();

                    await conn.CloseAsync();
                    Log.Information($"Archive count:{archiveIds.Count}\tRead DB:{sw1.ElapsedMilliseconds}\tCreate archive list:{sw2.ElapsedMilliseconds}\tinsert to archive time:{sw3.ElapsedMilliseconds}\t" +
                                    $"delete from outbox time:{sw4.ElapsedMilliseconds}");
                }

                dt.Dispose();
            }
        }
        catch (Exception e)
        {
            dt.Dispose();
            _errorCount++;
            _messageText = $"خطا در سرویس : {RuntimeSettings.ServiceName}{Environment.NewLine}{e.Message}";
            Log.Error($"Error in archive: {e.Message}");

            if (e.Message.Contains("Duplicate entry"))
            {
                Log.Information($"Trying to remove duplicate entry: {DateTime.Now}");

                await using (MySqlConnection con = new(RuntimeSettings.ConnectionString))
                {
                    await con.OpenAsync(token);

                    await using (MySqlCommand cm = new(string.Format(RuntimeSettings.DeleteQueryForDuplicateRecords, archiveTableName), con))
                    {
                        await cm.ExecuteNonQueryAsync(token);
                    }

                    await con.CloseAsync();
                }

                Log.Error($"Duplicate entry removed at: {DateTime.Now}");
            }
        }
    }

    private async Task<string> CreateArchiveTable(DateTime creationDate, CancellationToken token)
    {
        PersianCalendar persianCalendar = new();
        string archiveTableName = $"{RuntimeSettings.OutboxTableName}_Archive_{persianCalendar.GetYear(creationDate)}{persianCalendar.GetMonth(creationDate).ToString().PadLeft(2, '0')}";

        try
        {
            if (await ExistArchiveTable(archiveTableName))
            {
                return archiveTableName;
            }

            await using MySqlConnection connection = new(RuntimeSettings.ConnectionString);
            await connection.OpenAsync(token);

            string query = $"SHOW CREATE TABLE `{RuntimeSettings.OutboxTableName}`;";
            string createTableScript = "";

            await using (MySqlCommand command = new(query, connection))
            {
                await using (MySqlDataReader reader = command.ExecuteReader())
                {
                    if (await reader.ReadAsync(token))
                    {
                        createTableScript = reader.GetString(1); // ستون دوم حاوی اسکریپت است
                    }
                }
            }

            if (!string.IsNullOrEmpty(createTableScript))
            {
                // جایگزینی نام جدول قدیمی با نام جدید
                createTableScript = Regex.Replace(createTableScript, @$"\b{RuntimeSettings.OutboxTableName}\b", archiveTableName, RegexOptions.IgnoreCase);

                // اجرای اسکریپت برای ایجاد جدول جدید
                await using MySqlCommand createCommand = new(createTableScript, connection);
                await createCommand.ExecuteNonQueryAsync(token);
                Log.Error($"Table '{archiveTableName}' created successfully!");
            }
            else
            {
                Log.Error("Failed to retrieve table script.");
            }
            await connection.CloseAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"Error: {ex.Message}");
        }

        return archiveTableName;
    }

    private async Task<bool> ExistArchiveTable(string archiveTableName)
    {
        await using MySqlConnection conn = new(RuntimeSettings.ConnectionString);
        try
        {
            conn.Open();
            string query = $" SELECT COUNT(*) FROM information_schema.tables WHERE table_name = '{archiveTableName}'";
            await using MySqlCommand cmd = new(query, conn);
            int count = Convert.ToInt32(cmd.ExecuteScalar());
            
            await conn.CloseAsync();
            return count > 0;
        }
        catch (Exception ex)
        {
            Log.Error($"Error: {ex.Message}");
        }

        return false;
    }
    
    private async Task CopyFromOutgoingToOutboundAsync(CancellationToken token)
    {
        if (!RuntimeSettings.EnableCopyFromOutgoingToOutbound)
        {
            return;
        }
        
        Log.Information("start CopyFromOutgoingToOutbound");

        try
        {
            DataTable dataTable = new();
            string command = string.Format(RuntimeSettings.SelectQueryForOutgoing, RuntimeSettings.Tps);

            switch (RuntimeSettings.DbProvider)
            {
                case "SQL":
                {
                    await using SqlConnection cn = new(RuntimeSettings.ConnectionString);

                    await cn.OpenAsync(token);

                    try
                    {
                        await using SqlCommand cm = new(command, cn);
                        dataTable.Load(await cm.ExecuteReaderAsync(token));

                        if (dataTable.Rows.Count > 0)
                        {
                            string cmm = dataTable.Rows.Cast<DataRow>()
                                                  .Aggregate(string.Empty, (current, row) => current + string.Format(RuntimeSettings.InsertQueryForOutgoing, row["SOURCEADDRESS"], row["DESTINATIONADDRESS"], row["MESSAGETEXT"]));
                            RuntimeSettings.FullLog.OptionalLog(cmm);

                            await using SqlCommand cmInsert = new(cmm, cn);
                            cmInsert.CommandType = CommandType.Text;
                            await cmInsert.ExecuteNonQueryAsync(token);
                            cmInsert.Connection.Close();

                            List<string> ids = dataTable.Rows.Cast<DataRow>().Select(row => row["ID"].ToString()).ToList()!;

                            await using SqlCommand cmUpdate = new(string.Format(RuntimeSettings.UpdateQueryForOutgoing, string.Join(",", ids)), cn);
                            cmUpdate.CommandType = CommandType.Text;
                            await cmUpdate.ExecuteNonQueryAsync(token);
                            cmUpdate.Connection.Close();

                            await cn.CloseAsync();

                        }
                    }
                    catch (Exception e)
                    {
                        Log.Error($"Error in read db (CopyFromOutgoingToOutbound): {e.Message}");
                    }
                    finally
                    {
                        await cn.CloseAsync();
                    }

                    break;
                }

                case "MySQL":
                {
                    await using MySqlConnection mySqlConnection = new(RuntimeSettings.ConnectionString);

                    await mySqlConnection.OpenAsync(token);
                    MySqlTransaction sqlTransaction = await mySqlConnection.BeginTransactionAsync(token);
                    try
                    {

                        await using MySqlCommand cm = new(command, mySqlConnection);
                        dataTable.Load(cm.ExecuteReader());

                        if (dataTable.Rows.Count > 0)
                        {
                            string cmm = dataTable.Rows.Cast<DataRow>()
                                                  .Aggregate(string.Empty,
                                                             (current, row) => current +
                                                                               string.Format(RuntimeSettings.InsertQueryForOutgoing,
                                                                                             row["SOURCEADDRESS"],
                                                                                             row["DESTINATIONADDRESS"],
                                                                                             row["MESSAGETEXT"].ToString()!.Replace("'",  @""""),
                                                                                             DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                                                                                             row["ID"]
                                                                               ));

                            await using MySqlCommand cmInsert = new(cmm, mySqlConnection, sqlTransaction);
                            cmInsert.CommandType = CommandType.Text;
                            await cmInsert.ExecuteNonQueryAsync(token);

                            List<string> ids = dataTable.Rows.Cast<DataRow>().Select(row => row["ID"].ToString()).ToList()!;

                            await using MySqlCommand cmUpdate = new(string.Format(RuntimeSettings.UpdateQueryForOutgoing, string.Join(",", ids)), mySqlConnection, sqlTransaction);
                            cmUpdate.CommandType = CommandType.Text;
                            await cmUpdate.ExecuteNonQueryAsync(token);

                            await sqlTransaction.CommitAsync(token);
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Error($"Error in read db (CopyFromOutgoingToOutbound): {e.Message}");
                        await sqlTransaction.RollbackAsync(token);
                    }
                    finally
                    {
                        await mySqlConnection.CloseAsync();
                    }

                    break;
                }

                case "Oracle":
                {
                    await using OracleConnection oracleConnection = new(RuntimeSettings.ConnectionString);

                    await oracleConnection.OpenAsync(token);
                    OracleTransaction sqlTransaction = oracleConnection.BeginTransaction();
                    try
                    {

                        await using OracleCommand cm = new(command, oracleConnection);
                        dataTable.Load(await cm.ExecuteReaderAsync(token));

                        if (dataTable.Rows.Count > 0)
                        {
                            string cmm = dataTable.Rows.Cast<DataRow>()
                                                  .Aggregate(string.Empty,
                                                             (current, row) => current +
                                                                               string.Format(RuntimeSettings.InsertQueryForOutgoing,
                                                                                             row["SOURCEADDRESS"],
                                                                                             row["DESTINATIONADDRESS"],
                                                                                             row["MESSAGETEXT"].ToString()!.Replace("'",  @""""),
                                                                                             DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                                                                                             row["ID"]
                                                                               ));

                            await using OracleCommand cmInsert = new(cmm, oracleConnection);
                            cmInsert.CommandType = CommandType.Text;
                            await cmInsert.ExecuteNonQueryAsync(token);

                            List<string> ids = dataTable.Rows.Cast<DataRow>().Select(row => row["ID"].ToString()).ToList()!;

                            await using OracleCommand cmUpdate = new(string.Format(RuntimeSettings.UpdateQueryForOutgoing, string.Join(",", ids)), oracleConnection);
                            cmUpdate.CommandType = CommandType.Text;
                            await cmUpdate.ExecuteNonQueryAsync(token);

                            await sqlTransaction.CommitAsync(token);
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Error($"Error in read db (CopyFromOutgoingToOutbound): {e.Message}");
                        await sqlTransaction.RollbackAsync(token);
                    }
                    finally
                    {
                        await oracleConnection.CloseAsync();
                    }

                    break;
                }
            }
        }
        catch (Exception e)
        {
            Log.Error($"Error CopyFromOutgoingToOutbound : {e.Message}");
        }
    }

    private async Task CalculateNullStatusAsync(CancellationToken token)
    {
        if (DateTime.Now.TimeOfDay > RuntimeSettings.StartTime && DateTime.Now.TimeOfDay < RuntimeSettings.EndTime)
        {
            DataTable dt = new();

            switch (RuntimeSettings.DbProvider)
            {
                case "MySQL":
                {
                    await using MySqlConnection cn = new(RuntimeSettings.ConnectionString);
                    await cn.OpenAsync(token);

                    await using (MySqlCommand cm = new(RuntimeSettings.SelectQueryForNullStatus, cn))
                    {
                        dt.Load(await cm.ExecuteReaderAsync(token));
                    }

                    await cn.CloseAsync();

                    break;
                }

                case "Oracle":
                {
                    await using OracleConnection cn = new(RuntimeSettings.ConnectionString);
                    await cn.OpenAsync(token);

                    await using (OracleCommand cm = new(RuntimeSettings.SelectQueryForNullStatus, cn))
                    {
                        dt.Load(await cm.ExecuteReaderAsync(token));
                    }

                    await cn.CloseAsync();

                    break;
                }

                case "SQL":
                {
                    await using SqlConnection cn = new(RuntimeSettings.ConnectionString);
                    await cn.OpenAsync(token);

                    await using (SqlCommand cm = new(RuntimeSettings.SelectQueryForNullStatus, cn))
                    {
                        dt.Load(await cm.ExecuteReaderAsync(token));
                    }

                    await cn.CloseAsync();

                    break;
                }
            }

            if (Convert.ToInt32(dt.Rows[0]["count"].ToString()) > RuntimeSettings.QueueCount)
            {
                string messageText = $"اخطار بالا رفتن تعداد پیامهای در صف بر روی {RuntimeSettings.ServiceName}{Environment.NewLine}تعداد صف {dt.Rows[0]["count"]}";
                await CreateAlertMessage(messageText, false, token);
            }
        }
    }

    private async Task CreateAlertMessageAsync(CancellationToken token)
    {
        if (_errorCount > RuntimeSettings.TotalError)
        {
            await CreateAlertMessage(_messageText, true, token);
        }
    }

    private async Task CreateAlertMessage(string messageText, bool isError, CancellationToken token)
    {
        string[] dest = RuntimeSettings.DestinationAddress.Split(',');
        List<MessageSendModel> listToSend = [];

        listToSend.AddRange(dest.Select(item => new MessageSendModel
                                                {
                                                    DataCoding = HasUniCodeCharacter(messageText) ? (int)DataCodings.Ucs2 : (int)DataCodings.Default,
                                                    DestinationAddress = item,
                                                    HasUdh = false,
                                                    Udh = "",
                                                    SourceAddress = RuntimeSettings.SourceAddress,
                                                    MessageText = messageText + $"{Environment.NewLine}زمان : {DateTime.Now}"
                                                }));

        await SendAlert(listToSend, token);
        _errorCount = isError ? 0 : _errorCount;
    }

    private async Task SendAlert(List<MessageSendModel> listToSend, CancellationToken token)
    {
        try
        {
            // Try GetToken
            Log.Information("Start Getting token for send alerts");

            if (!RuntimeSettings.UseApiKey)
            {
                GetToken(token);
            }

            // Try sending Alerts
            HttpClient client = clientFactory.CreateClient();

            if (!RuntimeSettings.UseApiKey)
            {
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + _accessToken);
            }
            else
            {
                client.DefaultRequestHeaders.Add("X-API-Key", RuntimeSettings.ApiKey);
            }

            StringContent content = new(JsonConvert.SerializeObject(listToSend), Encoding.UTF8, MediaTypeNames.Application.Json);

            HttpResponseMessage response = await client.PostAsync(Url.Combine(RuntimeSettings.SmsEndPointBaseAddress, "api/message/send"), content, token);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                Log.Information("Send alert succeeded");
            }
            else if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (!RuntimeSettings.UseApiKey)
                {
                    Thread.Sleep(1000);
                    await SendAlert(listToSend, token);
                }
            }
            else
            {
                Log.Information($"status code: {response.StatusCode} message: {await response.Content.ReadAsStringAsync(token)}");
            }
        }
        catch (Exception e)
        {
            _errorCount++;
            _messageText = $"خطا در سرویس : {RuntimeSettings.ServiceName}{Environment.NewLine}{e.Message}";
            Log.Error(e.Message);
        }
    }
    
    public static async Task UpdateDbForDlr(List<UpdateDbModel> updateList, CancellationToken cancellationToken)
    {
        string cmm = string.Empty;
        switch (RuntimeSettings.DbProvider)
        {
            case "SQL":
            {
                await using SqlConnection cn = new(RuntimeSettings.ConnectionString);

                try
                {
                    await cn.OpenAsync(cancellationToken);
                    cmm = updateList.Select(item => string.Format(RuntimeSettings.UpdateQueryForDelivery, (int)item.Status, item.DeliveredAt, item.TrackingCode)).Aggregate(cmm, (current, newCommand) => current + newCommand);
                    Log.Error($"cmm: {cmm}");
                    await using SqlCommand cm = new(cmm, cn);
                    cm.CommandType = CommandType.Text;
                    await cm.ExecuteNonQueryAsync(cancellationToken);
                    await cm.Connection.CloseAsync();
                    await cn.CloseAsync();
                }
                catch (Exception e)
                {
                    await cn.CloseAsync();
                    _errorCount++;
                    _messageText = $"خطا در سرویس : {RuntimeSettings.ServiceName}{Environment.NewLine}{e.Message}";
                    Log.Error($"Error in Update. Error is: {e.Message}");
                }

                break;
            }

            case "MySQL":
            {
                await using MySqlConnection cn = new(RuntimeSettings.ConnectionString);

                try
                {
                    await cn.OpenAsync(cancellationToken);
                    cmm = updateList.Select(item => string.Format(RuntimeSettings.UpdateQueryForDelivery, item.Status, item.DeliveredAt, item.TrackingCode)).Aggregate(cmm, (current, newCommand) => current + newCommand);
                    await using MySqlCommand cm = new(cmm, cn) { CommandType = CommandType.Text };
                    await cm.ExecuteNonQueryAsync(cancellationToken);
                    await cm.Connection.CloseAsync();
                    await cn.CloseAsync();
                }
                catch (Exception e)
                {
                    await cn.CloseAsync();
                    _errorCount++;
                    _messageText = $"خطا در سرویس : {RuntimeSettings.ServiceName}{Environment.NewLine}{e.Message}";
                    Log.Error($"Error in Update. Error is: {e.Message}");
                }

                break;
            }
            
            case "Oracle":
            {
                await using OracleConnection cn = new(RuntimeSettings.ConnectionString);

                try
                {
                    await cn.OpenAsync(cancellationToken);
                    cmm = updateList.Select(item => string.Format(RuntimeSettings.UpdateQueryForDelivery, item.Status, item.DeliveredAt, item.TrackingCode)).Aggregate(cmm, (current, newCommand) => current + newCommand);
                    await using OracleCommand cm = new(cmm, cn);
                    cm.CommandType = CommandType.Text;
                    await cm.ExecuteNonQueryAsync(cancellationToken);
                    await cm.Connection.CloseAsync();
                    await cn.CloseAsync();
                }
                catch (Exception e)
                {
                    await cn.CloseAsync();
                    _errorCount++;
                    _messageText = $"خطا در سرویس : {RuntimeSettings.ServiceName}{Environment.NewLine}{e.Message}";
                    Log.Error($"Error in Update. Error is: {e.Message}");
                }

                break;
            }
        }
    }

    public static async Task InsertInboxAsync(List<MoDto> list, CancellationToken token)
    {
        string cmm = string.Empty;

        switch (RuntimeSettings.DbProvider)
        {
            case "SQL":
            {
                await using SqlConnection cn = new(RuntimeSettings.ConnectionString);

                try
                {
                    await cn.OpenAsync(token);
                    cmm = list.Select(item => string.Format(RuntimeSettings.InsertQueryForInbox, item.DestinationAddress, item.SourceAddress, item.ReceiveDateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), item.MessageText))
                              .Aggregate(cmm, (current, newCommand) => current + newCommand);

                    await using SqlCommand cm = new(cmm, cn);
                    cm.CommandType = CommandType.Text;
                    await cm.ExecuteNonQueryAsync(token);
                    cm.Connection.Close();
                    await cn.CloseAsync();
                }
                catch (Exception e)
                {
                    await cn.CloseAsync();
                    _errorCount++;
                    _messageText = $"خطا در سرویس : {RuntimeSettings.ServiceName}{Environment.NewLine}{e.Message}";
                    Log.Error($"Error in insert into inbound. Error is: {e.Message}");
                }

                break;
            }

            case "MySQL":
            {
                await using MySqlConnection cn = new(RuntimeSettings.ConnectionString);

                try
                {
                    await cn.OpenAsync(token);
                    cmm = list.Select(item => string.Format(RuntimeSettings.InsertQueryForInbox, item.DestinationAddress, item.SourceAddress, item.ReceiveDateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), item.MessageText))
                              .Aggregate(cmm, (current, newCommand) => current + newCommand);

                    await using MySqlCommand cm = new(cmm, cn) { CommandType = CommandType.Text };
                    await cm.ExecuteNonQueryAsync(token);
                    await cm.Connection.CloseAsync();
                    await cn.CloseAsync();
                }
                catch (Exception e)
                {
                    await cn.CloseAsync();
                    _errorCount++;
                    _messageText = $"خطا در سرویس : {RuntimeSettings.ServiceName}{Environment.NewLine}{e.Message}";
                    Log.Error($"Error in insert into inbound. Error is: {e.Message}");
                }

                break;
            }

            case "Oracle":
            {
                await using OracleConnection cn = new(RuntimeSettings.ConnectionString);

                try
                {
                    await cn.OpenAsync(token);
                    cmm = list.Select(item => string.Format(RuntimeSettings.InsertQueryForInbox, item.DestinationAddress, item.SourceAddress, item.ReceiveDateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), item.MessageText))
                              .Aggregate(cmm, (current, newCommand) => current + newCommand);

                    await using OracleCommand cm = new(cmm, cn);
                    cm.CommandType = CommandType.Text;
                    await cm.ExecuteNonQueryAsync(token);
                    await cm.Connection.CloseAsync();
                    await cn.CloseAsync();
                }
                catch (Exception e)
                {
                    await cn.CloseAsync();
                    _errorCount++;
                    _messageText = $"خطا در سرویس : {RuntimeSettings.ServiceName}{Environment.NewLine}{e.Message}";
                    Log.Error($"Error in insert into inbound. Error is: {e.Message}");
                }

                break;
            }
        }
    }

    private async Task ReadAndPostAsync(CancellationToken token)
    {
        if (!(DateTime.Now.TimeOfDay > RuntimeSettings.StartTime && DateTime.Now.TimeOfDay < RuntimeSettings.EndTime && RuntimeSettings.EnableSend))
        {
            return;
        }

        DataTable dt = new();

        try
        {
            Stopwatch sw1 = new();
            Stopwatch sw2 = new();
            Stopwatch sw3 = new();
            Stopwatch sw4 = new();
            Stopwatch total = new();
            sw1.Start();
            total.Start();

            string command = string.Format(RuntimeSettings.SelectQueryForSend, RuntimeSettings.Tps);
            //RuntimeSettings.FullLog.OptionalLog($"selectQueryForSend : {command}");

            switch (RuntimeSettings.DbProvider)
            {
                case "SQL":
                {
                    await using SqlConnection cn = new(RuntimeSettings.ConnectionString);

                    await cn.OpenAsync(token);

                    try
                    {
                        await using SqlCommand cm = new(command, cn);
                        dt.Load(await cm.ExecuteReaderAsync(token));
                    }
                    catch (Exception e)
                    {
                        Log.Error($"Error in read db (selectQueryForSend): {command} {Environment.NewLine}  {e.Message}");
                    }
                    finally
                    {
                        await cn.CloseAsync();
                    }

                    break;
                }

                case "MySQL":
                {
                    await using MySqlConnection cn = new(RuntimeSettings.ConnectionString);

                    await cn.OpenAsync(token);

                    try
                    {
                        await using MySqlCommand cm = new(command, cn);
                        dt.Load(await cm.ExecuteReaderAsync(token));
                    }
                    catch (Exception e)
                    {
                        Log.Error($"Error in read db (selectQueryForSend): {command} {Environment.NewLine} {e.Message}");
                    }
                    finally
                    {
                        await cn.CloseAsync();
                    }

                    break;
                }

                case "Oracle":
                {
                    await using OracleConnection cn = new(RuntimeSettings.ConnectionString);

                    await cn.OpenAsync(token);

                    try
                    {
                        await using OracleCommand cm = new(command, cn);
                        dt.Load(await cm.ExecuteReaderAsync(token));
                    }
                    catch (Exception e)
                    {
                        Log.Error($"Error in read db (selectQueryForSend): {command} {Environment.NewLine} {e.Message}");
                    }
                    finally
                    {
                        await cn.CloseAsync();
                    }

                    break;
                }
            }

            sw1.Stop();
            sw2.Start();

            try
            {
                if (dt.Rows.Count > 0)
                {
                    List<MessageSendModel> list = [];
                    List<List<MessageSendModel>> messageToSendDtos = [];
                    int tId = 0;

                    foreach (DataRow dr in dt.Rows)
                    {
                        MessageSendModel messageSendModel = new()
                                                            {
                                                                SourceAddress = dr["SOURCEADDRESS"].ToString()?.Replace("+", "")!,
                                                                DestinationAddress = dr["DESTINATIONADDRESS"].ToString()?.Replace("+", "")!,
                                                                MessageText = dr["MESSAGETEXT"].ToString()!,
                                                                Udh = dr["ID"].ToString()!,
                                                                DataCoding = HasUniCodeCharacter(dr["MESSAGETEXT"].ToString()!) ? 8 : 0
                                                            };

                        list.Add(messageSendModel);

                        if (list.Count >= RuntimeSettings.BatchSize)
                        {
                            messageToSendDtos.Add(list);
                            list = [];
                        }
                    }

                    if (list.Count > 0)
                    {
                        messageToSendDtos.Add(list);
                    }

                    if (RuntimeSettings.SendToWhiteList)
                    {
                        string mobiles = string.Join(",", messageToSendDtos.SelectMany(item => item.Select(m => $"'{m.DestinationAddress}'")).Distinct().ToList());
                        
                        string whiteListCommand = string.Format(RuntimeSettings.SelectQueryWhiteList, mobiles);
                        DataTable whiteListTable = new();
                        switch (RuntimeSettings.DbProvider)
                        {
                            case "SQL":
                            {
                                await using SqlConnection cn = new(RuntimeSettings.ConnectionString);

                                await cn.OpenAsync(token);
                                await using SqlCommand cm = new(whiteListCommand, cn);
                                cm.CommandType = CommandType.Text;
                                whiteListTable.Load(await cm.ExecuteReaderAsync(token));
                                await cm.Connection.CloseAsync();
                                await cn.CloseAsync();

                                break;
                            }

                            case "MySQL":
                            {
                                await using MySqlConnection cn = new(RuntimeSettings.ConnectionString);

                                await cn.OpenAsync(token);
                                await using MySqlCommand cm = new(whiteListCommand, cn) { CommandType = CommandType.Text };
                                whiteListTable.Load(await cm.ExecuteReaderAsync(token));
                                await cm.Connection.CloseAsync();
                                await cn.CloseAsync();

                                break;
                            }

                            case "Oracle":
                            {
                                await using OracleConnection cn = new(RuntimeSettings.ConnectionString);

                                await cn.OpenAsync(token);
                                await using OracleCommand cm = new(whiteListCommand, cn);
                                cm.CommandType = CommandType.Text;
                                whiteListTable.Load(await cm.ExecuteReaderAsync(token));
                                await cm.Connection.CloseAsync();
                                await cn.CloseAsync();

                                break;
                            }
                        }

                        List<string> whiteList = whiteListTable.Rows.Cast<DataRow>().Select(row => row["mobile"].ToString()).ToList()!;
                        
                        Log.Information($"======> whiteListCommand : {whiteListCommand}");
                        Log.Information($"======> whiteListTable : {JsonSerializer.Serialize(whiteListTable.Rows.Count)}");
                        Log.Information($"======> whiteList : {JsonSerializer.Serialize(whiteList)}");

                        List<MessageSendModel> tmpSend = [];
                        List<MessageSendModel> tmpReject = [];
                        foreach (MessageSendModel messageSendModel in messageToSendDtos.SelectMany(messageSendModels => messageSendModels))
                        {
                            if (whiteList.Count(m => messageSendModel.DestinationAddress == m) == 0)
                            {
                                tmpReject.Add(messageSendModel);
                            }
                            else
                            {
                                tmpSend.Add(messageSendModel);
                            }
                        }

                        messageToSendDtos = tmpSend.Count > 0 ? tmpSend.Chunk(RuntimeSettings.BatchSize).Select(s => s.ToList()).ToList() : [];

                        if (tmpReject.Count > 0)
                        {
                            await UpdateStatus(tmpReject.Select(r => r.Udh).ToList(), RuntimeSettings.StatusForFailedSend, token);
                        }
                        
                        Log.Information($"======> tmpReject : {JsonSerializer.Serialize(tmpReject)}");
                        Log.Information($"======> tmpSend : {JsonSerializer.Serialize(tmpSend)}");
                        Log.Information($"======> messageToSendDtos : {JsonSerializer.Serialize(messageToSendDtos)}");
                    }

                    sw2.Stop();

                    if (messageToSendDtos.Count == 0)
                    {
                        return;
                    }

                    sw3.Start();
                    List<string> ids = messageToSendDtos.SelectMany(item => item.Select(m => m.Udh)).ToList();
                    command = string.Format(RuntimeSettings.UpdateQueryBeforeSend, string.Join(",", ids));
                    RuntimeSettings.FullLog.OptionalLog($"updateQueryBeforeSend : {command}");

                    switch (RuntimeSettings.DbProvider)
                    {
                        case "SQL":
                        {
                            await using SqlConnection cn = new(RuntimeSettings.ConnectionString);

                            await cn.OpenAsync(token);
                            await using SqlCommand cm = new(command, cn);
                            cm.CommandType = CommandType.Text;
                            await cm.ExecuteNonQueryAsync(token);
                            await cm.Connection.CloseAsync();

                            await cn.CloseAsync();

                            break;
                        }

                        case "MySQL":
                        {
                            await using MySqlConnection cn = new(RuntimeSettings.ConnectionString);

                            await cn.OpenAsync(token);
                            await using MySqlCommand cm = new(command, cn) { CommandType = CommandType.Text };
                            await cm.ExecuteNonQueryAsync(token);
                            await cm.Connection.CloseAsync();
                            await cn.CloseAsync();

                            break;
                        }

                        case "Oracle":
                        {
                            await using OracleConnection cn = new(RuntimeSettings.ConnectionString);

                            await cn.OpenAsync(token);
                            await using OracleCommand cm = new(command, cn);
                            cm.CommandType = CommandType.Text;
                            await cm.ExecuteNonQueryAsync(token);
                            await cm.Connection.CloseAsync();
                            await cn.CloseAsync();

                            break;
                        }
                    }

                    sw3.Stop();
                    
                    sw4.Start();
                    List<Task> tasks = [];
                    foreach (List<MessageSendModel> item in messageToSendDtos)
                    {
                        tasks.Add(Task.Run(() => Send(item, tId++, token), token));
                    }
                    Task.WaitAll(tasks.ToArray(), token);

                    sw4.Stop();

                    total.Stop();
                    Log.Information($"Read count:{dt.Rows.Count}\tRead DB:{sw1.ElapsedMilliseconds}\tCreate send list:{sw2.ElapsedMilliseconds}\t Tasks: {tId}\t Update time: {sw3.ElapsedMilliseconds} \t Send time: {sw4.ElapsedMilliseconds} \t Total time: {total.ElapsedMilliseconds}");

                    dt.Dispose();
                }
            }
            catch (Exception ex)
            {
                dt.Dispose();
                _errorCount++;
                _messageText = $"خطا در سرویس : {RuntimeSettings.ServiceName}{Environment.NewLine}{ex.Message}";
                Log.Error($"Error in Read and Send. Error is: {ex.Message}");
            }
        }
        catch (Exception e)
        {
            _errorCount++;
            dt.Dispose();
            Log.Error($"Error in ReadAndPost: {e.Message}");
        }
    }
    
    private async Task Send(List<MessageSendModel> listToSend, int tId, CancellationToken token)
    {
        HttpClient client = clientFactory.CreateClient();

        if (!RuntimeSettings.UseApiKey)
        {
            client.DefaultRequestHeaders.Add("Authorization", "Bearer " + _accessToken);
        }
        else
        {
            client.DefaultRequestHeaders.Add("X-API-Key", RuntimeSettings.ApiKey);
        }

        byte[] data = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(listToSend));
        using MemoryStream ms = new ();
        await using (GZipStream gzip = new (ms, CompressionMode.Compress))
        {
            await gzip.WriteAsync(data, 0, data.Length, token);
        }
        byte[] compressed = ms.ToArray();

        ByteArrayContent content = new (compressed);
        content.Headers.ContentEncoding.Add("gzip");
        content.Headers.ContentType = new ("application/json");
        
        //StringContent content = new(JsonConvert.SerializeObject(listToSend), Encoding.UTF8, MediaTypeNames.Application.Json);
        
        List<string> ids = listToSend.Select(item => item.Udh).ToList();
        try
        {
            Stopwatch sw1 = new();
            Stopwatch sw2 = new();
            sw1.Start();
            HttpResponseMessage response = await client.PostAsync(Url.Combine(RuntimeSettings.SmsEndPointBaseAddress, $"api/{RuntimeSettings.ApiVersion}/message/send?returnLongId={RuntimeSettings.ReturnLongId}"), content, token);
            sw1.Stop();
            if (response.StatusCode == HttpStatusCode.OK)
            {
                if (ids.Count > 0)
                {
                    RuntimeSettings.FullLog.OptionalLog($"response content : {await response.Content.ReadAsStringAsync(token)}");
                    RuntimeSettings.FullLog.OptionalLog($"ids : {JsonConvert.SerializeObject(ids)}");
	                string command = string.Empty;

	                switch (RuntimeSettings.ApiVersion)
	                {
		                case 4:
		                {
			                ResultApiClass<List<ResultApiV4>>? batchIds = JsonConvert.DeserializeObject<ResultApiClass<List<ResultApiV4>>>(await response.Content.ReadAsStringAsync(token));

                                command = batchIds!.Data.Select((d, index) => string.Format(RuntimeSettings.UpdateQueryAfterSend,
                                                                                            d.Id.Length > 4 ? RuntimeSettings.StatusForSuccessSend : RuntimeSettings.StatusForFailedSend,  //  Status
                                                                                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), // SendDateTime
                                                                                            d.Id, // ReturnID
                                                                                            d.Part, //Length
                                                                                            d.UpstreamGateway, //UpstreamName
                                                                                            ids[index])) //Id
			                                   .Aggregate("", (current, newComm) => current + newComm);
			                break;
		                }

		                case 3 or 2:
		                {
			                ResultApiClass<List<KeyValuePair<string, string>>>? batchIds = JsonConvert.DeserializeObject<ResultApiClass<List<KeyValuePair<string, string>>>>(await response.Content.ReadAsStringAsync(token));

			                command = batchIds!.Data.Select((d, index) => string.Format(RuntimeSettings.UpdateQueryAfterSend,
                                                                                        d.Key.Length > 4 ? RuntimeSettings.StatusForSuccessSend : RuntimeSettings.StatusForFailedSend,  //  Status
                                                                                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), // SendDateTime
                                                                                        d.Key, // ReturnID
                                                                                        d.Value, //Length(v3) or mcc(v2)
                                                                                        ids[index])) //Id
			                                   .Aggregate("", (current, newComm) => current + newComm);

			                break;
		                }

		                case 1:
		                {
			                ResultApiClass<List<string>>? batchIds = JsonConvert.DeserializeObject<ResultApiClass<List<string>>>(await response.Content.ReadAsStringAsync(token));

			                command = batchIds!.Data.Select((d, index) => string.Format(RuntimeSettings.UpdateQueryAfterSend,
                                                                                        d.Length > 4 ? RuntimeSettings.StatusForSuccessSend : RuntimeSettings.StatusForFailedSend,  //  Status
                                                                                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), // SendDateTime
                                                                                        d, // ReturnID
                                                                                        ids[index])) //Id
			                                   .Aggregate("", (current, newComm) => current + newComm);
			                break;
		                }
	                }

                    try
                    {
                        sw2.Start();
                         
                        RuntimeSettings.FullLog.OptionalLog($"updateQueryAfterSend : {command}");

                        switch (RuntimeSettings.DbProvider)
                        {
                            case "SQL":
                            {
                                await using SqlConnection cn = new(RuntimeSettings.ConnectionString);

                                await cn.OpenAsync(token);
                                await using SqlCommand cm = new(command, cn);
                                cm.CommandType = CommandType.Text;
                                await cm.ExecuteNonQueryAsync(token);
                                await cm.Connection.CloseAsync();

                                await cn.CloseAsync();

                                break;
                            }

                            case "MySQL":
                            {
                                await using MySqlConnection cn = new(RuntimeSettings.ConnectionString);

                                await cn.OpenAsync(token);
                                await using MySqlCommand cm = new(command, cn) { CommandType = CommandType.Text };
                                await cm.ExecuteNonQueryAsync(token);
                                await cm.Connection.CloseAsync();
                                await cn.CloseAsync();

                                break;
                            }

                            case "Oracle":
                            {
                                await using OracleConnection cn = new(RuntimeSettings.ConnectionString);

                                await cn.OpenAsync(token);
                                await using OracleCommand cm = new(command, cn);
                                cm.CommandType = CommandType.Text;
                                await cm.ExecuteNonQueryAsync(token);
                                await cm.Connection.CloseAsync();
                                await cn.CloseAsync();

                                break;
                            }
                        }

                        sw2.Stop();
                        
                    }
                    catch (Exception e)
                    {
                        _errorCount++;
                        _messageText = $"خطا در سرویس : {RuntimeSettings.ServiceName}{Environment.NewLine}{e.Message}";
                        Log.Error($"Error in Update: {e.Message}");
                    }
                }
                Log.Information($"TaskId: {tId}\tSend time: {sw1.ElapsedMilliseconds}\t count:{listToSend.Count}\t Update count:{ids.Count}\tUpdate time:{sw2.ElapsedMilliseconds}");
            }
            else if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (!RuntimeSettings.UseApiKey)
                {
                    GetToken(token);
                    await Task.Delay(1000, token);
                    await Send(listToSend, tId, token);
                }
                else
                {
                    await UpdateStatus(ids, RuntimeSettings.StatusForFailedSend, token);
                }
            }
            else if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
	            await UpdateStatus(ids, RuntimeSettings.StatusForStored, token);
            }
            else
            {
                await UpdateStatus(ids, RuntimeSettings.StatusForFailedSend, token);
                RuntimeSettings.FullLog.OptionalLog($"status code: {response.StatusCode.ToString()} message: {await response.Content.ReadAsStringAsync(token)}");
            }
            
            Log.Information($"Update failed messages \tTaskId: {tId}\tSend time: {sw1.ElapsedMilliseconds}\t count:{listToSend.Count}\t Update count:{ids.Count}");
        }
        catch (Exception e)
        {
            await UpdateStatus(ids, RuntimeSettings.StatusForStored, token);
            _errorCount++;
            _messageText = $"خطا در سرویس : {RuntimeSettings.ServiceName}{Environment.NewLine}{e.Message}";
            Log.Error($"Send error : {e.Message}");
        }
    }

    private async Task UpdateStatus(List<string> ids, string status, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        switch (RuntimeSettings.DbProvider)
        {
            case "SQL":
            {
                await using SqlConnection cn = new(RuntimeSettings.ConnectionString);

                await cn.OpenAsync(cancellationToken);
                string pattern = RuntimeSettings.UpdateQueryAfterFailedSend;
                string command = ids.Select(id => string.Format(pattern, status, id)).Aggregate("", (current, newComm) => current + newComm);
                await using SqlCommand cm = new(command, cn);
                cm.CommandType = CommandType.Text;
                await cm.ExecuteNonQueryAsync(cancellationToken);
                await cm.Connection.CloseAsync();
                await cn.CloseAsync();

                break;
            }

            case "MySQL":
            {
                await using MySqlConnection cn = new(RuntimeSettings.ConnectionString);

                await cn.OpenAsync(cancellationToken);
                string pattern = RuntimeSettings.UpdateQueryAfterFailedSend;
                string command = ids.Select(id => string.Format(pattern, status, id)).Aggregate("", (current, newComm) => current + newComm);
                await using MySqlCommand cm = new(command, cn) { CommandType = CommandType.Text };
                await cm.ExecuteNonQueryAsync(cancellationToken);
                await cm.Connection.CloseAsync();
                await cn.CloseAsync();

                break;
            }
                
            case "Oracle":
            {
                await using OracleConnection cn = new(RuntimeSettings.ConnectionString);

                await cn.OpenAsync(cancellationToken);
                string pattern = RuntimeSettings.UpdateQueryAfterFailedSend;
                string command = ids.Select(id => string.Format(pattern, status, id)).Aggregate("", (current, newComm) => current + newComm);
                await using OracleCommand cm = new(command, cn);
                cm.CommandType = CommandType.Text;
                await cm.ExecuteNonQueryAsync(cancellationToken);
                await cm.Connection.CloseAsync();
                await cn.CloseAsync();

                break;
            }
        }
        stopwatch.Stop();

        Log.Information($"Update failed messages\t Update time:{stopwatch.ElapsedMilliseconds}");
    }

    private async Task GetDeliveryAsync(CancellationToken token)
    {
        if (!RuntimeSettings.EnableGetDlr)
        {
            return;
        }

        try
        {
            DataTable dataTable = new();

            int offset = 0;

            switch (RuntimeSettings.DbProvider)
            {
                case "SQL":
                {
                    await using SqlConnection cn = new(RuntimeSettings.ConnectionString);

                    await cn.OpenAsync(token);

                    try
                    {
                        bool hasRow = true;

                        while (hasRow)
                        {
                            await using SqlCommand cm = new(string.Format(RuntimeSettings.SelectQueryForGetDelivery, offset), cn);
                            dataTable.Load(await cm.ExecuteReaderAsync(token));

                            offset += 900;

                            hasRow = await UpdateDelivery1(dataTable);
                            dataTable = new();
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Error($"Error in read db (getDelivery): {e.Message}");
                    }
                    finally
                    {
                        await cn.CloseAsync();
                    }

                    break;
                }

                case "MySQL":
                {
                    await using MySqlConnection cn = new(RuntimeSettings.ConnectionString);

                    await cn.OpenAsync(token);

                    try
                    {
                        bool hasRow = true;

                        while (hasRow)
                        {
                            await using MySqlCommand cm = new(string.Format(RuntimeSettings.SelectQueryForGetDelivery, offset), cn);
                            dataTable.Load(await cm.ExecuteReaderAsync(token));

                            offset += 900;

                            hasRow = await UpdateDelivery1(dataTable);
                            dataTable = new();
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Error($"Error in read db (getDelivery): {e.Message}");
                    }
                    finally
                    {
                        await cn.CloseAsync();
                    }

                    break;
                }

                case "Oracle":
                {
                    await using OracleConnection cn = new(RuntimeSettings.ConnectionString);

                    await cn.OpenAsync(token);

                    try
                    {
                        bool hasRow = true;

                        while (hasRow)
                        {
                            await using OracleCommand cm = new(string.Format(RuntimeSettings.SelectQueryForGetDelivery, offset), cn);
                            dataTable.Load(await cm.ExecuteReaderAsync(token));

                            offset += 900;

                            hasRow = await UpdateDelivery1(dataTable);
                            dataTable = new();
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Error($"Error in read db (getDelivery): {e.Message}");
                    }
                    finally
                    {
                        await cn.CloseAsync();
                    }

                    break;
                }
            }

            async Task<HttpResponseMessage> SetResult(List<string> ids)
            {
                HttpClient client = clientFactory.CreateClient();

                if (!RuntimeSettings.UseApiKey)
                {
                    client.DefaultRequestHeaders.Add("Authorization", "Bearer " + _accessToken);
                }
                else
                {
                    client.DefaultRequestHeaders.Add("X-API-Key", RuntimeSettings.ApiKey);
                }

                StringContent content = new(JsonConvert.SerializeObject(ids), Encoding.UTF8, MediaTypeNames.Application.Json);

                return await client.PostAsync(Url.Combine(RuntimeSettings.SmsEndPointBaseAddress, $"api/message/GetDLR?returnLongId={RuntimeSettings.ReturnLongId}"), content, token);
            }

            async ValueTask<bool> UpdateDelivery1(DataTable table)
            {
                if (table.Rows.Count <= 0)
                {
                    return false;
                }

                List<string> ids = table.AsEnumerable().Select(m => m[0].ToString()).ToList()!;
                Loop:
                Stopwatch sw1 = Stopwatch.StartNew();
                HttpResponseMessage response = await SetResult(ids);
                sw1.Stop();
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    if (!RuntimeSettings.UseApiKey)
                    {
                        GetToken(token);
                        goto Loop;
                    }

                    if (RuntimeSettings.UseApiKey)
                    {
                        return false;
                    }
                }

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    ResultApiClass<List<DlrStatus>>? resultApi = JsonConvert.DeserializeObject<ResultApiClass<List<DlrStatus>>>(await response.Content.ReadAsStringAsync(token));
                    List<DlrStatus> statusList = resultApi!.Data;
                    List<DlrDto> initialList = [];

                    Log.Information($"statusList: {JsonConvert.SerializeObject(statusList)}");

                    for (int i = 0; i < table.Rows.Count; i++)
                    {
                        if (statusList.Any(s => s.Id == table.Rows[i][0].ToString()))
                        {
                            DlrStatus dlrStatus = statusList.First(s => s.Id == table.Rows[i][0].ToString());

                            initialList.AddRange(from dlrStatusPartStatus in dlrStatus.PartStatus
                                                 where dlrStatus.DeliveryStatus != DeliveryStatus.Sent
                                                 select new DlrDto
                                                        {
                                                            Status = dlrStatus.DeliveryStatus,
                                                            DateTime = dlrStatus.DeliveryDate!.Value.ToLocalTime().ToString(),
                                                            MessageId = dlrStatus.Id,
                                                            FullDelivery = dlrStatus.PartStatus.All(p => p.Item2 == DeliveryStatus.Delivered),
                                                            PartNumber = dlrStatusPartStatus.Item1
                                                        });
                        }
                    }

                    if (initialList.Any())
                    {
                        List<UpdateDbModel> updateList = [];
                        Stopwatch sw2 = new();
                        Stopwatch sw3 = new();
                        sw2.Start();
                        updateList.AddRange(initialList.Select(dto => new UpdateDbModel { Status = dto.Status, TrackingCode = dto.MessageId, DeliveredAt = Convert.ToDateTime(dto.DateTime).ToString("yyyy-MM-dd HH:mm:ss") }));
                        sw2.Stop();
                        sw3.Start();
                        await UpdateDbForDlr(updateList, token);
                        sw3.Stop();
                        Log.Information($"DLR - Api call time: {sw1.ElapsedMilliseconds}\t Create update list: {sw2.ElapsedMilliseconds}\t Update list count: {updateList.Count}\t update time: {sw3.ElapsedMilliseconds}");
                    }

                    
                    return true;
                }
                    
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    RuntimeSettings.FullLog.OptionalLog($"get dlr not found delivery for ids : {JsonConvert.SerializeObject(ids)}");

                    return true;
                }

                RuntimeSettings.FullLog.OptionalLog($"get dlr - api call error : {JsonConvert.SerializeObject(response)}");
               RuntimeSettings.FullLog.OptionalLog($"get dlr - ids : {JsonConvert.SerializeObject(ids)}");
                return true;
            }
        }
        catch (Exception e)
        {
            Log.Error($"Error GetDelivery : {e.Message}");
        }
    }

    private async Task GetMoAsync(CancellationToken token)
    {
        if (!RuntimeSettings.EnableGetMo)
        {
            return;
        }

        try
        {
            RuntimeSettings.FullLog.OptionalLog("start get mo");
            Stopwatch sw1 = Stopwatch.StartNew();
            List<MoDto> initialList = [];

            HttpClient client = clientFactory.CreateClient();

            if (!RuntimeSettings.UseApiKey)
            {
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + _accessToken);
            }
            else
            {
                client.DefaultRequestHeaders.Add("X-API-Key", RuntimeSettings.ApiKey);
            }

            HttpResponseMessage response = await client.GetAsync(Url.Combine(RuntimeSettings.SmsEndPointBaseAddress, "api/message/GetMO?returnId=true"), token);
            RuntimeSettings.FullLog.OptionalLog($"{JsonConvert.SerializeObject(await response.Content.ReadAsStringAsync(token))}");
            sw1.Stop();

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                RuntimeSettings.FullLog.OptionalLog("get mo not found item");
                return;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (!RuntimeSettings.UseApiKey)
                {
                    GetToken(token);
                }

                RuntimeSettings.FullLog.OptionalLog($"get mo push response status :{response.StatusCode}");
                return;
            }

            ResultApiClass<List<MoDto>>? moStatus = JsonConvert.DeserializeObject<ResultApiClass<List<MoDto>>>(await response.Content.ReadAsStringAsync(token));

            List<MoDto> inboxSaveQueueDtos = moStatus!.Data.Select(mo => new MoDto
                                                                         {
                                                                             DestinationAddress = mo.DestinationAddress,
                                                                             SourceAddress = mo.SourceAddress,
                                                                             ReceiveDateTime = mo.ReceiveDateTime,
                                                                             MessageText = mo.MessageText,
                                                                             Id = mo.Id
                                                                         })
                                                      .ToList();

            if (inboxSaveQueueDtos.Any())
            {
                initialList.AddRange(inboxSaveQueueDtos);

                List<MoDto> list = [];
                Stopwatch sw2 = new();
                Stopwatch sw3 = new();
                sw2.Start();

                list.AddRange(initialList.Select(dto => new MoDto { DestinationAddress = dto.DestinationAddress, SourceAddress = dto.SourceAddress, MessageText = dto.MessageText, ReceiveDateTime = dto.ReceiveDateTime }));

                sw2.Stop();
                sw3.Start();

                if (list.Any())
                {
                    await InsertInboxAsync(list, token);
                    sw3.Stop();
                    Log.Information($"MO - Api call time: {sw1.ElapsedMilliseconds}\t Create list: {sw2.ElapsedMilliseconds}\t list count: {list.Count}\t Insert time: {sw3.ElapsedMilliseconds}");
                }
            }
            await Task.Delay(1000, token);
        }
        catch (Exception ex)
        {
            Log.Error($"error get mo :{ex.Message}");
        }
    }
    
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Log.Information("Worker starting...");
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, CancellationTokenSource.Token);
            await base.StartAsync(linkedCts.Token);
        }
        catch (Exception ex)
        {
            Log.Error($"Error starting Worker {ex.Message}");
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Log.Information("Worker stopping...");
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, CancellationTokenSource.Token);
        await base.StopAsync(linkedCts.Token);
    }

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            Log.Information("Disposing Worker...");
            CancellationTokenSource.Cancel();
            StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            CancellationTokenSource.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error($"Error disposing Worker {ex.Message}");
        }
        _disposed = true;
    }

    public static bool HasUniCodeCharacter(string text)
    {
        return Regex.IsMatch(text, "[^\u0000-\u00ff]");
    }
}



public static class FullLogManage
{
    public static void OptionalLog(this bool logOption, string logContent)
    {
        if (logOption)
        {
            Log.Information(logContent);
        }
    }
}