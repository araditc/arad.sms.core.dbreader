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
using System.Data.SqlClient;
using System.Diagnostics;
using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.RegularExpressions;

using Arad.SMS.Core.WorkerForDownstreamGateway.DbReader.Models;

using Flurl;

using MySql.Data.MySqlClient;

using Newtonsoft.Json;

using Serilog;

namespace Arad.SMS.Core.WorkerForDownstreamGateway.DbReader;

public class Worker(IHttpClientFactory clientFactory) : BackgroundService
{
    private readonly CancellationTokenSource _cTs = new();
    private string _accessToken = "";
    private static int _errorCount;
    private static string _messageText = string.Empty;
    private bool _dlrFlag = true;
    private bool _errorCountFlag = true;
    private bool _flag = true;
    private bool _archiveFlag = true;
    private bool _moFlag = true;
    private bool _copyFromOutgoingToOutboundFlag = true;
    private DateTime _flagTime;
    private Timer? _nullTimer;
    private Timer? _sendTimer;
    private Timer? _moTimer;
    private Timer? _dlrTimer;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            Log.Information("Start service");
            RuntimeSettings.LoadSetting(null);
            
            if (!RuntimeSettings.UseApiKey)
            {
                GetToken();
            }
            
            Log.Information(RuntimeSettings.DbProvider);

            _sendTimer = new(OnSend, null, 1000, 1000);
            _moTimer = new(OnMo, null, 1000, RuntimeSettings.MOIntervalTime * 60 * 1000);
            _dlrTimer = new(OnDlr, null, 1000, RuntimeSettings.DLRIntervalTime * 60 * 1000);
            _nullTimer = new(CalculateNullStatus, null, 1000, RuntimeSettings.IntervalTime * 60 * 1000);
        }
        catch (Exception e)
        {
            Log.Error(e.Message);
        }

        return Task.CompletedTask;
    }

    private async void GetToken()
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
            HttpResponseMessage response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            TokenResponseModel? data = await response.Content.ReadFromJsonAsync<TokenResponseModel>();
            _accessToken = data?.AccessToken!;
            Log.Information($"Token: {_accessToken}");
        }
        catch (Exception e)
        {
            Log.Error($"Error in getting token: {e.Message}");
        }
    }

    private async void OnSend(object? stateInfo)
    {
        try
        {
            if (_flag == false && (DateTime.Now - _flagTime).TotalSeconds > 300)
            {
                _flag = true;
            }

            if (DateTime.Now.TimeOfDay > RuntimeSettings.StartTime && DateTime.Now.TimeOfDay < RuntimeSettings.EndTime && _flag && RuntimeSettings.EnableSend)
            {
                _flagTime = DateTime.Now;
                ReadAndPost();
            }

            if (_errorCount > RuntimeSettings.TotalError && _errorCountFlag)
            {
                await CreateAlertMessage(_messageText, true);
            }

            if (_copyFromOutgoingToOutboundFlag && RuntimeSettings.EnableCopyFromOutgoingToOutbound)
            {
                await CopyFromOutgoingToOutbound();
            }

            if (_archiveFlag)
            {
                ArchiveData();
            }
        }
        catch (Exception e)
        {
            Log.Error(e.Message);
        }
    }

    private async void ArchiveData()
    {
        _archiveFlag = false;
        DataTable dt = new();

        try
        {
            Stopwatch sw1 = new();
            Stopwatch sw2 = new();
            Stopwatch sw3 = new();
            Stopwatch sw4 = new();

            if (RuntimeSettings.ArchiveEnable)
            {
                if (DateTime.Now.TimeOfDay > RuntimeSettings.ArchiveStartTime && DateTime.Now.TimeOfDay < RuntimeSettings.ArchiveEndTime)
                {
                    await using MySqlConnection cn = new(RuntimeSettings.ConnectionString);

                    cn.Open();
                    sw1.Start();

                    await using (MySqlCommand cm = new(string.Format(RuntimeSettings.SelectQueryForArchive!, RuntimeSettings.ArchiveBatchSize), cn))
                    {
                        dt.Load(cm.ExecuteReader());
                    }

                    sw1.Stop();
                    await cn.CloseAsync();

                    List<string> archiveIds = [];

                    if (dt.Rows.Count > 0)
                    {
                        sw2.Start();

                        foreach (DataRow dr in dt.Rows)
                        {
                            archiveIds.Add(dr["ID"].ToString()!);
                        }

                        sw2.Stop();

                        if (archiveIds.Any())
                        {
                            sw3.Start();
                            string ids = string.Join(",", archiveIds);
                            await using MySqlConnection con = new(RuntimeSettings.ConnectionString);
                            con.Open();
                            string command = string.Format(RuntimeSettings.InsertQueryForArchive!, ids);
                            await using MySqlCommand cm = new(command, con) { CommandType = CommandType.Text };
                            cm.ExecuteNonQuery();
                            await cm.Connection.CloseAsync();
                            await con.CloseAsync();
                            sw3.Stop();
                            sw4.Start();

                            await using MySqlConnection conn = new(RuntimeSettings.ConnectionString);
                            conn.Open();
                            string deleteCommand = string.Format(RuntimeSettings.DeleteQueryAfterArchive!, ids);
                            await using MySqlCommand dlCommand = new(deleteCommand, conn) { CommandType = CommandType.Text };
                            dlCommand.ExecuteNonQuery();
                            await dlCommand.Connection.CloseAsync();
                            sw4.Stop();

                            await conn.CloseAsync();
                            Log.Information(
                                $"Archive count:{archiveIds.Count}\tRead DB:{sw1.ElapsedMilliseconds}\tCreate archive list:{sw2.ElapsedMilliseconds}\tinsert to archive time:{sw3.ElapsedMilliseconds}\tdelete from outbox time:{sw4.ElapsedMilliseconds}");
                        }

                        dt.Dispose();
                    }
                }
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
                    con.Open();

                    await using (MySqlCommand cm = new(RuntimeSettings.DeleteQueryForDuplicateRecords, con))
                    {
                        cm.ExecuteNonQuery();
                    }

                    await con.CloseAsync();
                }

                Log.Error($"Duplicate entry removed at: {DateTime.Now}");
            }
        }

        _archiveFlag = true;
    }

    private async void OnMo(object? stateInfo)
    {
        try
        {
            if (_moFlag && RuntimeSettings.EnableGetMo)
            {
                await GetMo();
            }
        }
        catch (Exception e)
        {
            Log.Error(e.Message);
        }
    }
    
    private async void OnDlr(object? stateInfo)
    {
        try
        {
            if (_dlrFlag && RuntimeSettings.EnableGetDlr)
            {
                await GetDelivery();
            }
        }
        catch (Exception e)
        {
            Log.Error(e.Message);
        }
    }

    private async ValueTask CopyFromOutgoingToOutbound()
    {
        _copyFromOutgoingToOutboundFlag = false;
        
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

                    cn.Open();

                    try
                    {
                        await using SqlCommand cm = new(command, cn);
                        dataTable.Load(await cm.ExecuteReaderAsync());

                        if (dataTable.Rows.Count > 0)
                        {
                            string cmm = dataTable.Rows.Cast<DataRow>()
                                                  .Aggregate(string.Empty, (current, row) => current + string.Format(RuntimeSettings.InsertQueryForOutgoing, row["SOURCEADDRESS"], row["DESTINATIONADDRESS"], row["MESSAGETEXT"]));
                            RuntimeSettings.FullLog.OptionalLog(cmm);

                            await using SqlCommand cmInsert = new(cmm, cn);
                            cmInsert.CommandType = CommandType.Text;
                            cmInsert.ExecuteNonQuery();
                            cmInsert.Connection.Close();

                            List<string> ids = dataTable.Rows.Cast<DataRow>().Select(row => row["ID"].ToString()).ToList()!;

                            await using SqlCommand cmUpdate = new(string.Format(RuntimeSettings.UpdateQueryForOutgoing, string.Join(",", ids)), cn);
                            cmUpdate.CommandType = CommandType.Text;
                            cmUpdate.ExecuteNonQuery();
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

                    mySqlConnection.Open();
                    MySqlTransaction sqlTransaction = await mySqlConnection.BeginTransactionAsync();
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
                            cmInsert.ExecuteNonQuery();

                            List<string> ids = dataTable.Rows.Cast<DataRow>().Select(row => row["ID"].ToString()).ToList()!;

                            await using MySqlCommand cmUpdate = new(string.Format(RuntimeSettings.UpdateQueryForOutgoing, string.Join(",", ids)), mySqlConnection, sqlTransaction);
                            cmUpdate.CommandType = CommandType.Text;
                            cmUpdate.ExecuteNonQuery();

                            await sqlTransaction.CommitAsync();
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Error($"Error in read db (CopyFromOutgoingToOutbound): {e.Message}");
                        await sqlTransaction.RollbackAsync();
                    }
                    finally
                    {
                        await mySqlConnection.CloseAsync();
                    }

                    break;
                }
            }

        }
        catch (Exception e)
        {
            Log.Error($"Error CopyFromOutgoingToOutbound : {e.Message}");
        }

        _copyFromOutgoingToOutboundFlag = true;
    }

    private async void CalculateNullStatus(object? stateInfo)
    {
        if (DateTime.Now.TimeOfDay > RuntimeSettings.StartTime && DateTime.Now.TimeOfDay < RuntimeSettings.EndTime)
        {
            DataTable dt = new();

            switch (RuntimeSettings.DbProvider)
            {
                case "MySQL":
                {
                    await using MySqlConnection cn = new(RuntimeSettings.ConnectionString);
                    cn.Open();

                    await using (MySqlCommand cm = new(RuntimeSettings.SelectQueryForNullStatus, cn))
                    {
                        dt.Load(await cm.ExecuteReaderAsync());
                    }

                    await cn.CloseAsync();

                    break;
                }

                case "SQL":
                {
                    await using SqlConnection cn = new(RuntimeSettings.ConnectionString);
                    cn.Open();

                    await using (SqlCommand cm = new(RuntimeSettings.SelectQueryForNullStatus, cn))
                    {
                        dt.Load(await cm.ExecuteReaderAsync());
                    }

                    await cn.CloseAsync();

                    break;
                }
            }

            if (Convert.ToInt32(dt.Rows[0]["count"].ToString()) > RuntimeSettings.QueueCount)
            {
                string messageText = $"اخطار بالا رفتن تعداد پیامهای در صف بر روی {RuntimeSettings.ServiceName}{Environment.NewLine}تعداد صف {dt.Rows[0]["count"]}";
                await CreateAlertMessage(messageText, false);
            }
        }
    }

    private async Task CreateAlertMessage(string messageText, bool isError)
    {
        _errorCountFlag = false;
        string[] dest = RuntimeSettings.DestinationAddress.Split(',');
        List<MessageDto> listToSend = [];

        listToSend.AddRange(dest.Select(item => new MessageDto
                                                {
                                                    DataCoding = HasUniCodeCharacter(messageText) ? (int)DataCodings.Ucs2 : (int)DataCodings.Default,
                                                    DestinationAddress = item,
                                                    HasUdh = false,
                                                    Udh = "",
                                                    SourceAddress = RuntimeSettings.SourceAddress,
                                                    MessageText = messageText + $"{Environment.NewLine}زمان : {DateTime.Now}"
                                                }));

        await SendAlert(listToSend);
        _errorCount = isError ? 0 : _errorCount;
        _errorCountFlag = true;
    }

    private async Task SendAlert(List<MessageDto> listToSend)
    {
        try
        {
            // Try GetToken
            Log.Information("Start Getting token for send alerts");

            if (!RuntimeSettings.UseApiKey)
            {
                GetToken();
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

            HttpResponseMessage response = await client.PostAsync(Url.Combine(RuntimeSettings.SmsEndPointBaseAddress, "api/message/send"), content);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                Log.Information("Send alert succeeded");
            }
            else if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (!RuntimeSettings.UseApiKey)
                {
                    Thread.Sleep(1000);
                    await SendAlert(listToSend);
                }
            }
            else
            {
                Log.Information($"status code: {response.StatusCode} message: {await response.Content.ReadAsStringAsync()}");
            }
        }
        catch (Exception e)
        {
            _errorCount++;
            _messageText = $"خطا در سرویس : {RuntimeSettings.ServiceName}{Environment.NewLine}{e.Message}";
            Log.Error(e.Message);
        }
    }
    
    public static async void UpdateDbForDlr(List<UpdateDbModel> updateList)
    {
        string cmm = string.Empty;
        switch (RuntimeSettings.DbProvider)
        {
            case "SQL":
            {
                await using SqlConnection cn = new(RuntimeSettings.ConnectionString);

                try
                {
                    cn.Open();
                    cmm = updateList.Select(item => string.Format(RuntimeSettings.UpdateQueryForDelivery, (int)item.Status, item.DeliveredAt, item.TrackingCode)).Aggregate(cmm, (current, newCommand) => current + newCommand);
                    Log.Error($"cmm: {cmm}");
                    await using SqlCommand cm = new(cmm, cn);
                    cm.CommandType = CommandType.Text;
                    await cm.ExecuteNonQueryAsync();
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
                    cn.Open();
                    cmm = updateList.Select(item => string.Format(RuntimeSettings.UpdateQueryForDelivery, item.Status, item.DeliveredAt, item.TrackingCode)).Aggregate(cmm, (current, newCommand) => current + newCommand);
                    await using MySqlCommand cm = new(cmm, cn) { CommandType = CommandType.Text };
                    await cm.ExecuteNonQueryAsync();
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

    public static async void InsertInboxAsync(List<MoDto> list)
    {
        string cmm = string.Empty;

        switch (RuntimeSettings.DbProvider)
        {
            case "SQL":
            {
                await using SqlConnection cn = new(RuntimeSettings.ConnectionString);

                try
                {
                    cn.Open();
                    cmm = list.Select(item => string.Format(RuntimeSettings.InsertQueryForInbox, item.DestinationAddress, item.SourceAddress, item.ReceiveDateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), item.MessageText))
                              .Aggregate(cmm, (current, newCommand) => current + newCommand);

                    await using SqlCommand cm = new(cmm, cn);
                    cm.CommandType = CommandType.Text;
                    cm.ExecuteNonQuery();
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
                    cn.Open();
                    cmm = list.Select(item => string.Format(RuntimeSettings.InsertQueryForInbox, item.DestinationAddress, item.SourceAddress, item.ReceiveDateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), item.MessageText))
                              .Aggregate(cmm, (current, newCommand) => current + newCommand);

                    await using MySqlCommand cm = new(cmm, cn) { CommandType = CommandType.Text };
                    cm.ExecuteNonQuery();
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

    private async void ReadAndPost()
    {
        _flag = false;
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
            RuntimeSettings.FullLog.OptionalLog($"selectQueryForSend : {command}");

            switch (RuntimeSettings.DbProvider)
            {
                case "SQL":
                {
                    await using SqlConnection cn = new(RuntimeSettings.ConnectionString);

                    cn.Open();

                    try
                    {
                        await using SqlCommand cm = new(command, cn);
                        dt.Load(await cm.ExecuteReaderAsync());
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

                    cn.Open();

                    try
                    {
                        await using MySqlCommand cm = new(command, cn);
                        dt.Load(await cm.ExecuteReaderAsync());
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
                    List<MessageDto> list = [];
                    List<string> ids = [];
                    List<MessageToSendDto> messageToSendDtos = [];
                    int tId = 0;

                    foreach (DataRow dr in dt.Rows)
                    {
                        MessageDto dto = new()
                                         {
                                             SourceAddress = dr["SOURCEADDRESS"].ToString()?.Replace("+", "")!,
                                             DestinationAddress = dr["DESTINATIONADDRESS"].ToString()?.Replace("+", "")!,
                                             MessageText = dr["MESSAGETEXT"].ToString()!,
                                             Udh = dr["ID"].ToString()!,
                                             DataCoding = HasUniCodeCharacter(dr["MESSAGETEXT"].ToString()!) ? 8 : 0
                                         };

                        list.Add(dto);
                        ids.Add(dr["ID"].ToString()!);

                        if (list.Count >= RuntimeSettings.BatchSize)
                        {
                            messageToSendDtos.Add(new() { MessageDtos = list, IdsList = ids });
                            list = [];
                        }
                    }

                    if (list.Count > 0)
                    {
                        messageToSendDtos.Add(new() { MessageDtos = list, IdsList = ids });
                    }

                    sw2.Stop();

                    sw3.Start();
                    command = string.Format(RuntimeSettings.UpdateQueryBeforeSend, string.Join(",", ids));
                    RuntimeSettings.FullLog.OptionalLog($"updateQueryBeforeSend : {command}");

                    switch (RuntimeSettings.DbProvider)
                    {
                        case "SQL":
                        {
                            await using SqlConnection cn = new(RuntimeSettings.ConnectionString);

                            cn.Open();
                            await using SqlCommand cm = new(command, cn);
                            cm.CommandType = CommandType.Text;
                            await cm.ExecuteNonQueryAsync();
                            await cm.Connection.CloseAsync();

                            await cn.CloseAsync();

                            break;
                        }

                        case "MySQL":
                        {
                            await using MySqlConnection cn = new(RuntimeSettings.ConnectionString);

                            cn.Open();
                            await using MySqlCommand cm = new(command, cn) { CommandType = CommandType.Text };
                            await cm.ExecuteNonQueryAsync();
                            await cm.Connection.CloseAsync();
                            await cn.CloseAsync();

                            break;
                        }
                    }

                    sw3.Stop();
                    
                    sw4.Start();
                    List<Task> tasks = [];
                    foreach (MessageToSendDto item in messageToSendDtos)
                    {
                        tasks.Add(Task.Run(()=> Send(item.MessageDtos, item.IdsList, tId++)));
                    }
                    Task.WaitAll(tasks.ToArray());

                    sw4.Stop();

                    total.Stop();
                    Log.Information($"Read count:{dt.Rows.Count}\tRead DB:{sw1.ElapsedMilliseconds}\tCreate send list:{sw2.ElapsedMilliseconds}\t Tasks: {tId}\t Utime: {sw3.ElapsedMilliseconds} \t Stime: {sw4.ElapsedMilliseconds} \t Total time: {total.ElapsedMilliseconds}");

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

        _flag = true;
    }
    
    private async ValueTask Send(List<MessageDto> listToSend, List<string> ids, int tId)
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

        StringContent content = new(JsonConvert.SerializeObject(listToSend), Encoding.UTF8, MediaTypeNames.Application.Json);

        try
        {
            Stopwatch sw1 = new();
            Stopwatch sw2 = new();
            sw1.Start();
            HttpResponseMessage response = await client.PostAsync(Url.Combine(RuntimeSettings.SmsEndPointBaseAddress, $"api/{RuntimeSettings.ApiVersion}/message/send?returnLongId={RuntimeSettings.ReturnLongId}"), content);
            sw1.Stop();
            if (response.StatusCode == HttpStatusCode.OK)
            {
                if (ids.Count > 0)
                {
                    RuntimeSettings.FullLog.OptionalLog($"response content : {await response.Content.ReadAsStringAsync()}");
                    RuntimeSettings.FullLog.OptionalLog($"ids : {JsonConvert.SerializeObject(ids)}");
	                string command = string.Empty;

	                switch (RuntimeSettings.ApiVersion)
	                {
		                case 4:
		                {
			                ResultApiClass<List<ResultApiV4>>? batchIds = JsonConvert.DeserializeObject<ResultApiClass<List<ResultApiV4>>>(await response.Content.ReadAsStringAsync());

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
			                ResultApiClass<List<KeyValuePair<string, string>>>? batchIds = JsonConvert.DeserializeObject<ResultApiClass<List<KeyValuePair<string, string>>>>(await response.Content.ReadAsStringAsync());

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
			                ResultApiClass<List<string>>? batchIds = JsonConvert.DeserializeObject<ResultApiClass<List<string>>>(await response.Content.ReadAsStringAsync());

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

                                cn.Open();
                                await using SqlCommand cm = new(command, cn);
                                cm.CommandType = CommandType.Text;
                                await cm.ExecuteNonQueryAsync();
                                await cm.Connection.CloseAsync();

                                await cn.CloseAsync();

                                break;
                            }

                            case "MySQL":
                            {
                                await using MySqlConnection cn = new(RuntimeSettings.ConnectionString);

                                cn.Open();
                                await using MySqlCommand cm = new(command, cn) { CommandType = CommandType.Text };
                                await cm.ExecuteNonQueryAsync();
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
                    GetToken();
                    await Task.Delay(1000);
                    await Send(listToSend, ids, tId);
                }
                else
                {
                    await UpdateStatus(RuntimeSettings.StatusForFailedSend);
                }
            }
            else if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
	            await UpdateStatus(RuntimeSettings.StatusForStored);
            }
            else
            {
                await UpdateStatus(RuntimeSettings.StatusForFailedSend);
                RuntimeSettings.FullLog.OptionalLog($"status code: {response.StatusCode.ToString()} message: {await response.Content.ReadAsStringAsync()}");
            }

            async ValueTask UpdateStatus(string status)
            {
                switch (RuntimeSettings.DbProvider)
                {
                    case "SQL":
                    {
                        await using SqlConnection cn = new(RuntimeSettings.ConnectionString);

                        cn.Open();
                        sw2.Start();
                        string pattern = RuntimeSettings.UpdateQueryAfterFailedSend;
                        string command = ids.Select(id => string.Format(pattern, status, id)).Aggregate("", (current, newComm) => current + newComm);
                        await using SqlCommand cm = new(command, cn);
                        cm.CommandType = CommandType.Text;
                        await cm.ExecuteNonQueryAsync();
                        await cm.Connection.CloseAsync();
                        await cn.CloseAsync();

                        break;
                    }

                    case "MySQL":
                    {
                        await using MySqlConnection cn = new(RuntimeSettings.ConnectionString);

                        cn.Open();
                        sw2.Start();
                        string pattern = RuntimeSettings.UpdateQueryAfterFailedSend;
                        string command = ids.Select(id => string.Format(pattern, status, id)).Aggregate("", (current, newComm) => current + newComm);
                        await using MySqlCommand cm = new(command, cn) { CommandType = CommandType.Text };
                        await cm.ExecuteNonQueryAsync();
                        await cm.Connection.CloseAsync();
                        await cn.CloseAsync();

                        break;
                    }
                }

                Log.Information($"Update failed messages \tTaskId: {tId}\tSend time: {sw1.ElapsedMilliseconds}\t count:{listToSend.Count}\t Update count:{ids.Count}\tUpdate time:{sw2.ElapsedMilliseconds}");

            }
        }
        catch (Exception e)
        {
            _errorCount++;
            _messageText = $"خطا در سرویس : {RuntimeSettings.ServiceName}{Environment.NewLine}{e.Message}";
            Log.Error($"Send error : {e.Message}");
        }
    }

    private async Task GetDelivery()
    {
        _dlrFlag = false;

        try
        {
            DataTable dataTable = new();

            int offset = 0;

            switch (RuntimeSettings.DbProvider)
            {
                case "SQL":
                {
                    await using SqlConnection cn = new(RuntimeSettings.ConnectionString);

                    cn.Open();

                    try
                    {
                        bool hasRow = true;

                        while (hasRow)
                        {
                            await using SqlCommand cm = new(string.Format(RuntimeSettings.SelectQueryForGetDelivery, offset), cn);
                            dataTable.Load(await cm.ExecuteReaderAsync());

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

                    cn.Open();

                    try
                    {
                        bool hasRow = true;

                        while (hasRow)
                        {
                            await using MySqlCommand cm = new(string.Format(RuntimeSettings.SelectQueryForGetDelivery, offset), cn);
                            dataTable.Load(await cm.ExecuteReaderAsync());

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

                return await client.PostAsync(Url.Combine(RuntimeSettings.SmsEndPointBaseAddress, $"api/message/GetDLR?returnLongId={RuntimeSettings.ReturnLongId}"), content);
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
                        GetToken();
                        goto Loop;
                    }

                    if (RuntimeSettings.UseApiKey)
                    {
                        return false;
                    }
                }

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    ResultApiClass<List<DlrStatus>>? resultApi = JsonConvert.DeserializeObject<ResultApiClass<List<DlrStatus>>>(await response.Content.ReadAsStringAsync());
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
                        UpdateDbForDlr(updateList);
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

        _dlrFlag = true;
    }

    private async Task GetMo()
    {
        _moFlag = false;

        while (!_moFlag)
        {
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

                HttpResponseMessage response = await client.GetAsync(Url.Combine(RuntimeSettings.SmsEndPointBaseAddress, "api/message/GetMO?returnId=true"));
                RuntimeSettings.FullLog.OptionalLog($"{JsonConvert.SerializeObject(await response.Content.ReadAsStringAsync())}");
                sw1.Stop();

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    _moFlag = true;
                    RuntimeSettings.FullLog.OptionalLog("get mo not found item");
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    if (!RuntimeSettings.UseApiKey)
                    {
                        GetToken();
                    }

                    RuntimeSettings.FullLog.OptionalLog($"get mo push response status :{response.StatusCode}");

                    continue;
                }

                ResultApiClass<List<MoDto>>? moStatus = JsonConvert.DeserializeObject<ResultApiClass<List<MoDto>>>(await response.Content.ReadAsStringAsync());

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
                        InsertInboxAsync(list);
                        sw3.Stop();
                        Log.Information($"MO - Api call time: {sw1.ElapsedMilliseconds}\t Create list: {sw2.ElapsedMilliseconds}\t list count: {list.Count}\t Insert time: {sw3.ElapsedMilliseconds}");
                    }
                }
                await Task.Delay(1000);
            }
            catch (Exception ex)
            {
                Log.Error($"error get mo :{ex.Message}");
            }
        }

        _moFlag = true;
    }
    
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _cTs.Cancel();
        Task.WaitAll();
        Log.Information($"Stop service at: {DateTime.Now}");

        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _cTs.Dispose();
        base.Dispose();
    }

    public static bool HasUniCodeCharacter(string text)
    {
        return Regex.IsMatch(text, "[^\u0000-\u00ff]");
    }
}

public class MessageToSendDto
{
    public List<MessageDto> MessageDtos { get; set; } = [];

    public List<string> IdsList { get; set; } = [];
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