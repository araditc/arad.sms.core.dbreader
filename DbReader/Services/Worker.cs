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
using System.Data.Common;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.RegularExpressions;

using Arad.SMS.Core.DbReader.Factory;
using Arad.SMS.Core.DbReader.Models;

using Flurl;

using Newtonsoft.Json;

using Serilog;

//using MySqlConnector;

namespace Arad.SMS.Core.DbReader.Services;

public class Worker(IHttpClientFactory clientFactory, IDbConnectionFactory connectionFactory) : BackgroundService
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
        Log.Information("Started job : {JobName}", job.Name);

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
                    Log.Information("Delaying {JobName} until aligned time: {NextRunTime}", job.Name, nextRunTime);
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
                Log.Error("{JobName} canceled.", job.Name);
                break;
            }
            catch (Exception ex)
            {
                failureCount++;
                Log.Information(ex, "Error in job {JobName} (failure #{FailureCount})", job.Name, failureCount);

                if (failureCount >= maxFailures)
                {
                    //failureCount = 0;//TODO
                    Log.Error("Job {JobName} failed {FailureCount} times. Triggering fallback/alerting and stopping.", job.Name, failureCount);
                    //await TriggerJobAlertAsync(job, ex, token);
                    
                    //break;
                }
            }
        }
        
        Log.Information("Stopped job : {JobName}", job.Name);
    }

    private async void GetToken(CancellationToken token)
    {
        try
        {
            Log.Information("Start getting token");
            HttpClient client = clientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(RuntimeSettings.Timeout);

            HttpRequestMessage request = new(HttpMethod.Post, $"{RuntimeSettings.SmsEndPointBaseAddress}/connect/token");
            MultipartFormDataContent multipartContent = new();
            multipartContent.Add(new StringContent(RuntimeSettings.UserName, Encoding.UTF8, MediaTypeNames.Text.Plain), "username");
            multipartContent.Add(new StringContent(RuntimeSettings.Password, Encoding.UTF8, MediaTypeNames.Text.Plain), "password");

            request.Content = multipartContent;
            HttpResponseMessage response = await client.SendAsync(request, token);
            response.EnsureSuccessStatusCode();

            TokenResponseModel? data = await response.Content.ReadFromJsonAsync<TokenResponseModel>(cancellationToken: token);
            _accessToken = data?.AccessToken!;
            Log.Information("Token: {AccessToken}", _accessToken);
        }
        catch (Exception e)
        {
            Log.Error("Error in getting token: {EMessage}", e.Message);
        }
    }
    
    private async Task ArchiveDataAsync(CancellationToken token)
    {
        if (!(RuntimeSettings.ArchiveEnable && DateTime.Now.TimeOfDay > RuntimeSettings.ArchiveStartTime && DateTime.Now.TimeOfDay < RuntimeSettings.ArchiveEndTime))
        {
            return;
        }
        
        await using DbConnection connection = await connectionFactory.CreateOpenConnectionAsync(token);
        DbCommand dbCommand;
        DataTable dataTable = new();
        string archiveTableName = $"{RuntimeSettings.OutboxTableName}_Archive"; //default name

        try
        {
            Stopwatch sw1 = new();
            Stopwatch sw2 = new();
            Stopwatch sw3 = new();
            Stopwatch sw4 = new();
            
            sw1.Start();
            
            dbCommand = connectionFactory.CreateCommand(string.Format(RuntimeSettings.SelectQueryForArchive, RuntimeSettings.ArchiveBatchSize), connection);
            dataTable.Load(await dbCommand.ExecuteReaderAsync(token));
            
            sw1.Stop();

            List<string> archiveIds = [];

            if (dataTable.Rows.Count > 0)
            {
                sw2.Start();

                DateTime? creationDate = dataTable.Rows[0].Field<DateTime>("CreationDate");
                if (creationDate != null)
                {
                    archiveTableName = await connectionFactory.CreateArchiveTable(creationDate.Value, token);
                }

                archiveIds.AddRange(from DataRow dr in dataTable.Rows select dr["ID"].ToString()!);

                sw2.Stop();

                if (archiveIds.Any())
                {
                    sw3.Start();

                    string command = string.Format(RuntimeSettings.InsertQueryForArchive, archiveTableName, string.Join(",", archiveIds));
                    dbCommand = connectionFactory.CreateCommand(command, connection);
                    await dbCommand.ExecuteNonQueryAsync(token);

                    sw3.Stop();

                    sw4.Start();
                    
                    string deleteCommand = string.Format(RuntimeSettings.DeleteQueryAfterArchive, string.Join(",", archiveIds));
                    dbCommand = connectionFactory.CreateCommand(deleteCommand, connection);
                    await dbCommand.ExecuteNonQueryAsync(token);

                    sw4.Stop();

                    Log.Information($"Archive count:{archiveIds.Count}\tRead DB:{sw1.ElapsedMilliseconds}\tCreate archive list:{sw2.ElapsedMilliseconds}\tinsert to archive time:{sw3.ElapsedMilliseconds}\t" +
                                    $"delete from outbox time:{sw4.ElapsedMilliseconds}");
                }

                dataTable.Dispose();
            }
        }
        catch (Exception e)
        {
            dataTable.Dispose();
            _errorCount++;
            _messageText = $"خطا در سرویس : {RuntimeSettings.ServiceName}{Environment.NewLine}{e.Message}";
            Log.Error("Error in archive: {EMessage}", e.Message);

            if (e.Message.Contains("Duplicate entry"))
            {
                Log.Information("Trying to remove duplicate entry: {DateTime}", DateTime.Now);
                
                dbCommand = connectionFactory.CreateCommand(string.Format(RuntimeSettings.DeleteQueryForDuplicateRecords, archiveTableName), connection);
                await dbCommand.ExecuteNonQueryAsync(token);

                Log.Error("Duplicate entry removed at: {DateTime}", DateTime.Now);
            }
        }
    }
    
    private async Task CalculateNullStatusAsync(CancellationToken token)
    {
        if (DateTime.Now.TimeOfDay > RuntimeSettings.StartTime && DateTime.Now.TimeOfDay < RuntimeSettings.EndTime)
        {
            DataTable dataTable = new();

            await using DbConnection connection = await connectionFactory.CreateOpenConnectionAsync(token);
            DbCommand dbCommand = connectionFactory.CreateCommand(RuntimeSettings.SelectQueryForNullStatus, connection);
            dataTable.Load(await dbCommand.ExecuteReaderAsync(token));
            
            if (Convert.ToInt32(dataTable.Rows[0]["count"].ToString()) > RuntimeSettings.QueueCount)
            {
                string messageText = $"اخطار بالا رفتن تعداد پیامهای در صف بر روی {RuntimeSettings.ServiceName}{Environment.NewLine}تعداد صف {dataTable.Rows[0]["count"]}";
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
            client.Timeout = TimeSpan.FromSeconds(RuntimeSettings.Timeout);

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
                Log.Information("status code: {ResponseStatusCode} message: {ReadAsStringAsync}", response.StatusCode, await response.Content.ReadAsStringAsync(token));
            }
        }
        catch (Exception e)
        {
            _errorCount++;
            _messageText = $"خطا در سرویس : {RuntimeSettings.ServiceName}{Environment.NewLine}{e.Message}";
            Log.Error(e.Message);
        }
    }
    
    private async Task UpdateDbForDlr(List<UpdateDbModel> updateList, CancellationToken token)
    {
        try
        {
            await using DbConnection connection = await connectionFactory.CreateOpenConnectionAsync(token);

            string strCommand = string.Empty;
            strCommand = updateList.Select(item => string.Format(RuntimeSettings.UpdateQueryForDelivery, (int)item.Status, item.DeliveredAt, item.TrackingCode)).Aggregate(strCommand, (current, newCommand) => current + newCommand);
            
            DbCommand command = connectionFactory.CreateCommand(strCommand, connection);
            await command.ExecuteNonQueryAsync(token);
        }
        catch (Exception e)
        {
            _errorCount++;
            _messageText = $"خطا در سرویس : {RuntimeSettings.ServiceName}{Environment.NewLine}{e.Message}";
            Log.Error("Error in Update. Error is: {EMessage}", e.Message);
        }
    }
    
    private async Task InsertInboxAsync(List<MoDto> list, CancellationToken token)
    {
        try
        {
            await using DbConnection connection = await connectionFactory.CreateOpenConnectionAsync(token);

            string strCommand = string.Empty;
            strCommand = list.Select(item => string.Format(RuntimeSettings.InsertQueryForInbox, item.DestinationAddress, item.SourceAddress, item.ReceiveDateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), item.MessageText)).
                              Aggregate(strCommand, (current, newCommand) => current + newCommand);

            DbCommand command = connectionFactory.CreateCommand(strCommand, connection);
            await command.ExecuteNonQueryAsync(token);
        }
        catch (Exception e)
        {
            _errorCount++;
            _messageText = $"خطا در سرویس : {RuntimeSettings.ServiceName}{Environment.NewLine}{e.Message}";
            Log.Error("Error in insert into inbound. Error is: {EMessage}", e.Message);
        }
    }
    
    private async Task ReadAndPostAsync(CancellationToken token)
    {
        if (!(DateTime.Now.TimeOfDay > RuntimeSettings.StartTime && DateTime.Now.TimeOfDay < RuntimeSettings.EndTime && RuntimeSettings.EnableSend))
        {
            return;
        }

        DataTable dataTable = new();

        try
        {
            Dictionary<string, object> parameters = new();
            Stopwatch total = Stopwatch.StartNew();

            Stopwatch selectStopwatch = Stopwatch.StartNew();

            await using DbConnection connection = await connectionFactory.CreateOpenConnectionAsync(token);

            string strCommand = !RuntimeSettings.SelectQueryForSend.StartsWith("SP:") ? string.Format(RuntimeSettings.SelectQueryForSend, RuntimeSettings.Tps) : RuntimeSettings.SelectQueryForSend.Remove(0, 3);
            if (RuntimeSettings.SelectQueryForSend.StartsWith("SP:"))
            {
                parameters.Add("@mps", RuntimeSettings.Tps);
            }
            DbCommand command = connectionFactory.CreateCommand(strCommand, connection, parameters);

            
            dataTable.Load(await command.ExecuteReaderAsync(token));
            
            selectStopwatch.Stop();
            
            Stopwatch createSendListStopwatch = new();

            if (dataTable.Rows.Count > 0)
            {
                List<MessageSendModel> list = [];
                List<List<MessageSendModel>> messageToSendDtos = [];
                int tId = 0;

                foreach (DataRow dr in dataTable.Rows)
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

                    command = connectionFactory.CreateCommand(whiteListCommand, connection);
                    whiteListTable.Load(await command.ExecuteReaderAsync(token));
                        
                    List<string> whiteList = whiteListTable.Rows.Cast<DataRow>().Select(row => row["mobile"].ToString()).ToList()!;

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
                }

                createSendListStopwatch.Stop();

                if (messageToSendDtos.Count == 0)
                {
                    return;
                }
                    
                Stopwatch updateQueryBeforeSendStopwatch = Stopwatch.StartNew();

                List<string> ids = messageToSendDtos.SelectMany(item => item.Select(m => m.Udh)).ToList();
                strCommand = string.Format(RuntimeSettings.UpdateQueryBeforeSend, string.Join(",", ids));
                RuntimeSettings.FullLog.OptionalLog($"updateQueryBeforeSend : {strCommand}");

                command = connectionFactory.CreateCommand(strCommand, connection);
                await command.ExecuteNonQueryAsync(token);
                    
                updateQueryBeforeSendStopwatch.Stop();

                Stopwatch sendStopwatch = Stopwatch.StartNew();

                List<Task> tasks = [];
                foreach (List<MessageSendModel> item in messageToSendDtos)
                {
                    tasks.Add(Task.Run(() => Send(item, tId++, token), token));
                }
                Task.WaitAll(tasks.ToArray(), TimeSpan.FromMinutes(3));

                sendStopwatch.Stop();

                total.Stop();

                Log.Information("Read count:{RowsCount}\tRead DB:{Sw1ElapsedMilliseconds}\tCreate send list:{Sw2ElapsedMilliseconds}\t Tasks: {TId}\t Update time: {Sw3ElapsedMilliseconds} \t " +
                                "Send time: {Sw4ElapsedMilliseconds} \t Total time: {TotalElapsedMilliseconds}",
                                dataTable.Rows.Count,
                                selectStopwatch.ElapsedMilliseconds,
                                createSendListStopwatch.ElapsedMilliseconds,
                                tId,
                                updateQueryBeforeSendStopwatch.ElapsedMilliseconds,
                                sendStopwatch.ElapsedMilliseconds,
                                total.ElapsedMilliseconds);

                dataTable.Dispose();
            }
        }
        catch (Exception e)
        {
            _messageText = $"خطا در سرویس : {RuntimeSettings.ServiceName}{Environment.NewLine}{e.Message}";
            _errorCount++;
            dataTable.Dispose();
            Log.Error("Error in ReadAndPost: {EMessage}", e.Message);
        }
    }
    
    private async Task Send(List<MessageSendModel> listToSend, int tId, CancellationToken token)
    {
        HttpClient client = clientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(RuntimeSettings.Timeout);

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
                        
                        await using DbConnection connection = await connectionFactory.CreateOpenConnectionAsync(token);
                        DbCommand dbCommand = connectionFactory.CreateCommand(command, connection);
                        await dbCommand.ExecuteNonQueryAsync(token);
                        
                        sw2.Stop();
                    }
                    catch (Exception e)
                    {
                        _errorCount++;
                        _messageText = $"خطا در سرویس : {RuntimeSettings.ServiceName}{Environment.NewLine}{e.Message}";
                        Log.Error("Error in Update: {EMessage}", e.Message);
                    }
                }
                Log.Information("TaskId: {TId}\tSend time: {Sw1ElapsedMilliseconds}\t count:{Count}\t Update count:{IdsCount}\tUpdate time:{Sw2ElapsedMilliseconds}", tId, sw1.ElapsedMilliseconds, listToSend.Count, ids.Count, sw2.ElapsedMilliseconds);
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
            
            Log.Information("Update failed messages \tTaskId: {TId}\tSend time: {Sw1ElapsedMilliseconds}\t count:{Count}\t Update count:{IdsCount}", tId, sw1.ElapsedMilliseconds, listToSend.Count, ids.Count);
        }
        catch (Exception e)
        {
            await UpdateStatus(ids, RuntimeSettings.StatusForStored, token);
            _errorCount++;
            _messageText = $"خطا در سرویس : {RuntimeSettings.ServiceName}{Environment.NewLine}{e.Message}";
            Log.Error("Send error : {EMessage}", e.Message);
        }
    }

    private async Task UpdateStatus(List<string> ids, string status, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        await using DbConnection connection = await connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        string strCommand = ids.Select(id => string.Format(RuntimeSettings.UpdateQueryAfterFailedSend, status, id)).Aggregate("", (current, newComm) => current + newComm);
        
        DbCommand dbCommand = connectionFactory.CreateCommand(strCommand, connection);
        await dbCommand.ExecuteNonQueryAsync(cancellationToken);
        
        stopwatch.Stop();

        Log.Information("Update failed messages\t Update time:{StopwatchElapsedMilliseconds}", stopwatch.ElapsedMilliseconds);
    }

    private async Task GetDeliveryAsync(CancellationToken token)
    {
        if (!RuntimeSettings.EnableGetDlr)
        {
            return;
        }

        try
        {
            int offset = 0;
            await using DbConnection connection = await connectionFactory.CreateOpenConnectionAsync(token);

            try
            {
                bool hasRow = true;

                while (hasRow)
                {
                    DataTable dataTable = new();
                    DbCommand dbCommand = connectionFactory.CreateCommand(string.Format(RuntimeSettings.SelectQueryForGetDelivery, offset), connection);
                    dataTable.Load(await dbCommand.ExecuteReaderAsync(token));

                    offset += 900;

                    hasRow = await UpdateDelivery1(dataTable);
                }
            }
            catch (Exception e)
            {
                Log.Error("Error in read db (getDelivery): {EMessage}", e.Message);
            }
            
            async Task<HttpResponseMessage> SetResult(List<string> ids)
            {
                HttpClient client = clientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(RuntimeSettings.Timeout);

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

            async Task<bool> UpdateDelivery1(DataTable table)
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

                switch (response.StatusCode)
                {
                    case HttpStatusCode.Unauthorized:
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

                        break;
                    }
                    case HttpStatusCode.OK:
                    {
                        ResultApiClass<List<DlrStatus>>? resultApi = JsonConvert.DeserializeObject<ResultApiClass<List<DlrStatus>>>(await response.Content.ReadAsStringAsync(token));
                        List<DlrStatus> statusList = resultApi!.Data;
                        List<DlrDto> initialList = [];

                        Log.Information("statusList: {SerializeObject}", JsonConvert.SerializeObject(statusList));

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
                                                                PartNumber = dlrStatusPartStatus.Item1,
                                                                Mobile = ""
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
                            Log.Information("DLR - Api call time: {Sw1ElapsedMilliseconds}\t Create update list: {Sw2ElapsedMilliseconds}\t Update list count: {UpdateListCount}\t update time: {Sw3ElapsedMilliseconds}", sw1.ElapsedMilliseconds, sw2.ElapsedMilliseconds, updateList.Count, sw3.ElapsedMilliseconds);
                        }

                    
                        return true;
                    }
                    case HttpStatusCode.NotFound:
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
            Log.Error("Error GetDelivery : {EMessage}", e.Message);
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
            client.Timeout = TimeSpan.FromSeconds(RuntimeSettings.Timeout);

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
                    Log.Information("MO - Api call time: {Sw1ElapsedMilliseconds}\t Create list: {Sw2ElapsedMilliseconds}\t list count: {ListCount}\t Insert time: {Sw3ElapsedMilliseconds}", sw1.ElapsedMilliseconds, sw2.ElapsedMilliseconds, list.Count, sw3.ElapsedMilliseconds);
                }
            }
            await Task.Delay(1000, token);
        }
        catch (Exception ex)
        {
            Log.Error("error get mo :{ExMessage}", ex.Message);
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
            Log.Error("Error starting Worker {ExMessage}", ex.Message);
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
            Log.Error("Error disposing Worker {ExMessage}", ex.Message);
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
