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

using System.Collections.Concurrent;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Text;
using System.Text.RegularExpressions;

using Arad.SMS.Core.DbReader.Models;

using Flurl;

using MySql.Data.MySqlClient;

using Newtonsoft.Json;

using Serilog;

namespace Arad.SMS.Core.DbReader;

public class Worker(IHttpClientFactory clientFactory) : BackgroundService
{
    public static List<DlrDto> CurrentDeliverySaveQueues = [];
    public static ConcurrentQueue<DlrDto> DlrEntranceQueue = new();
    public static ConcurrentQueue<MoDto> MoEntranceQueue = new();
    private static int _totalError = 10;
    private static int _intervalTime = 5;
    private static int _queueCount = 5000;
    private readonly CancellationTokenSource _cTs = new();
    private string _accessToken;
    private string _apiKey;
    private int _batchSize;
    private string _connectionString = "";
    private string _dbProvider = "MySQL";
    private string _destinationAddress;
    private bool _dlrFlag = true;
    private bool _enableGetDlr = true;
    private bool _enableGetMo = true;
    private bool _enableSend = true;
    private TimeSpan _endTime;
    private int _errorCount;
    private bool _errorCountFlag = true;
    private bool _flag = true;
    private DateTime _flagTime;
    private string _insertQueryForInbox;
    private string _messageText;
    private bool _moFlag = true;
    private Timer _nullTimer;
    private string _password;
    private bool _returnLongId;
    private bool _saveDlrFlag = true;
    private bool _saveMoFlag = true;
    private string _selectQueryForGetDelivery;
    private string _selectQueryForNullStatus;
    private string _selectQueryForSend;
    private string _serviceName;
    private Timer _settingTimer;
    private string _smsEndPointBaseAddress;
    private string _sourceAddress;
    private TimeSpan _startTime;
    private string _statusForFailedSend;
    private string _statusForSuccessSend;
    private Timer _timer;
    private int _tps;
    private string _updateQueryAfterFailedSend;
    private string _updateQueryAfterSend;
    private string _updateQueryBeforeSend;
    private string _updateQueryForDelivery;
    private bool _useApiKey;
    private string _userName;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            Log.Information("Start service");
            LoadSetting(null);
            ReadFromFile(); 
            
            if (!_useApiKey)
            {
                GetToken();
            }

            _timer = new(OnTimedEvent, null, 1000, 1000);
            _nullTimer = new(CalculateNullStatus, null, 1000, _intervalTime * 60 * 1000);
            _settingTimer = new(LoadSetting, null, 1000, 1 * 60 * 1000);
        }
        catch (Exception e)
        {
            Log.Error(e.Message);
        }

        return Task.CompletedTask;
    }

    private void ReadFromFile()
    {
        try
        {
            if (File.Exists(Path.Combine(Program.LogConfig.LogFileAddressDirectory, "DLRList.txt")))
            {
                List<DlrDto> deliverySaveQueueDtos = [];

                StreamReader sr = new(Program.LogConfig.LogFileAddressDirectory + "DLRList.txt");
                string? line = sr.ReadLine();

                if (!string.IsNullOrWhiteSpace(line))
                {
                    deliverySaveQueueDtos.Add(JsonConvert.DeserializeObject<DlrDto>(line));
                }

                while (line != null)
                {
                    line = sr.ReadLine();

                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        deliverySaveQueueDtos.Add(JsonConvert.DeserializeObject<DlrDto>(line));
                    }
                }

                if (deliverySaveQueueDtos.Count > 0)
                {
                    foreach (DlrDto item in deliverySaveQueueDtos)
                    {
                        DlrEntranceQueue.Enqueue(item);
                    }
                }

                sr.Close();
                File.Delete(Path.Combine(Program.LogConfig.LogFileAddressDirectory, "DLRList.txt"));
            }
        }
        catch (Exception e)
        {
            Log.Error("Exception: " + e.Message);
        }
    }

    private void WriteToFile()
    {
        try
        {
            List<DlrDto> deliverySaveQueueDtos = [];

            if (!DlrEntranceQueue.IsEmpty)
            {
                while (!DlrEntranceQueue.IsEmpty)
                {
                    DlrEntranceQueue.TryDequeue(out DlrDto? outboxDto);

                    if (outboxDto is not null)
                    {
                        deliverySaveQueueDtos.Add(outboxDto);
                    }
                }
            }

            if (CurrentDeliverySaveQueues.Count > 0)
            {
                foreach (DlrDto item in CurrentDeliverySaveQueues)
                {
                    deliverySaveQueueDtos.Add(item);
                }
            }

            if (deliverySaveQueueDtos.Count > 0)
            {
                StreamWriter sw = new(Program.LogConfig.LogFileAddressDirectory + "DLRList.txt");

                foreach (DlrDto item in deliverySaveQueueDtos)
                {
                    sw.WriteLine(JsonConvert.SerializeObject(item));
                }

                sw.Close();
            }
        }
        catch (Exception e)
        {
            Log.Error("Exception: " + e.Message);
        }
    }

    private void LoadSetting(object? stateInfo)
    {
        if (Program.Configuration != null)
        {
            _serviceName = Program.Configuration["ServiceName"] ?? "";
            _batchSize = Convert.ToInt32(Program.Configuration["Message:BatchSize"]);
            _tps = Convert.ToInt32(Program.Configuration["Message:TPS"]);
            _enableSend = Convert.ToBoolean(Program.Configuration["Message:EnableSend"]);
            _enableGetDlr = Convert.ToBoolean(Program.Configuration["Message:EnableGetDLR"]);
            _enableGetMo = Convert.ToBoolean(Program.Configuration["Message:EnableGetMO"]);
            _dbProvider = Program.Configuration["DB:Provider"] ?? "";
            _connectionString = Program.Configuration["DB:ConnectionString"] ?? "";
            _selectQueryForSend = Program.Configuration["DB:SelectQueryForSend"] ?? "";
            _updateQueryAfterSend = Program.Configuration["DB:UpdateQueryAfterSend"] ?? "";
            _updateQueryBeforeSend = Program.Configuration["DB:UpdateQueryBeforeSend"] ?? "";
            _statusForSuccessSend = Program.Configuration["DB:StatusForSuccessSend"] ?? "";
            _updateQueryAfterFailedSend = Program.Configuration["DB:UpdateQueryAfterFailedSend"] ?? "";
            _statusForFailedSend = Program.Configuration["DB:StatusForFailedSend"] ?? "";
            _selectQueryForGetDelivery = Program.Configuration["DB:SelectQueryForGetDelivery"] ?? "";
            _updateQueryForDelivery = Program.Configuration["DB:UpdateQueryForDelivery"] ?? "";
            _insertQueryForInbox = Program.Configuration["DB:InsertQueryForInbox"] ?? "";
            _selectQueryForNullStatus = Program.Configuration["DB:SelectQueryForNullStatus"] ?? "";

            #region API Setting
            _userName = Program.Configuration["SmsEndPointConfig:UserName"] ?? "";
            _password = Program.Configuration["SmsEndPointConfig:Password"] ?? "";
            _smsEndPointBaseAddress = Program.Configuration["SmsEndPointConfig:SmsEndPointBaseAddress"] ?? "";
            _useApiKey = Convert.ToBoolean(Program.Configuration["SmsEndPointConfig:UseApiKey"]);
            _apiKey = Program.Configuration["SmsEndPointConfig:ApiKey"] ?? "";
            _returnLongId = Convert.ToBoolean(Program.Configuration["SmsEndPointConfig:ReturnLongId"]);
            #endregion

            #region Bulk time setting
            _startTime = TimeSpan.Parse(Program.Configuration["BulkTimeSettings:Start"] ?? string.Empty);
            _endTime = TimeSpan.Parse(Program.Configuration["BulkTimeSettings:End"] ?? string.Empty);
            #endregion

            #region Allert setting
            _sourceAddress = Program.Configuration["AlertSetting:SourceAddress"] ?? "";
            _destinationAddress = Program.Configuration["AlertSetting:DestinationAddress"] ?? "";
            _totalError = Convert.ToInt32(Program.Configuration["AlertSetting:ErrorCount"]);
            _intervalTime = Convert.ToInt32(Program.Configuration["AlertSetting:IntervalTime"]);
            _queueCount = Convert.ToInt32(Program.Configuration["AlertSetting:QueueCount"]);
        }
        #endregion
    }

    private async void GetToken()
    {
        try
        {
            Log.Information("Start getting token");
            HttpClient client = clientFactory.CreateClient();

            HttpRequestMessage request = new(HttpMethod.Post, $"{_smsEndPointBaseAddress}/connect/token");
            MultipartFormDataContent multipartContent = new();
            multipartContent.Add(new StringContent(_userName, Encoding.UTF8, MediaTypeNames.Text.Plain), "username");
            multipartContent.Add(new StringContent(_password, Encoding.UTF8, MediaTypeNames.Text.Plain), "password");

            request.Content = multipartContent;
            HttpResponseMessage response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            TokenResponseModel? data = await response.Content.ReadFromJsonAsync<TokenResponseModel>();
            _accessToken = data?.AccessToken;
            Log.Information($"Token: {_accessToken}");
        }
        catch (Exception e)
        {
            Log.Error($"Error in getting token: {e.Message}");
        }
    }

    private async void OnTimedEvent(object? stateInfo)
    {
        try
        {
            if (_flag == false && (DateTime.Now - _flagTime).TotalSeconds > 300)
            {
                _flag = true;
            }

            if (DateTime.Now.TimeOfDay > _startTime && DateTime.Now.TimeOfDay < _endTime && _flag && _enableSend)
            {
                _flagTime = DateTime.Now;
                ReadAndPost();
            }

            if (_saveDlrFlag)
            {
                UpdateDelivery();
            }

            if (_saveMoFlag)
            {
                UpdateReceive();
            }

            if (_errorCount > _totalError && _errorCountFlag)
            {
                await CreateAlertMessage(_messageText, true);
            }

            if (_dlrFlag && _enableGetDlr)
            {
                await GetDelivery();
            }

            if (_moFlag && _enableGetMo)
            {
                await GetMo();
            }
        }
        catch (Exception e)
        {
            Log.Error(e.Message);
        }
    }

    private async void CalculateNullStatus(object? stateInfo)
    {
        if (DateTime.Now.TimeOfDay > _startTime && DateTime.Now.TimeOfDay < _endTime)
        {
            DataTable dt = new();

            switch (_dbProvider)
            {
                case "MySQL":
                {
                    await using MySqlConnection cn = new(_connectionString);
                    cn.Open();

                    await using (MySqlCommand cm = new(_selectQueryForNullStatus, cn))
                    {
                        dt.Load(await cm.ExecuteReaderAsync());
                    }

                    await cn.CloseAsync();

                    break;
                }

                case "SQL":
                {
                    await using SqlConnection cn = new(_connectionString);
                    cn.Open();

                    await using (SqlCommand cm = new(_selectQueryForNullStatus, cn))
                    {
                        dt.Load(await cm.ExecuteReaderAsync());
                    }

                    await cn.CloseAsync();

                    break;
                }
            }

            if (Convert.ToInt32(dt.Rows[0]["count"].ToString()) > _queueCount)
            {
                string messageText = $"اخطار بالا رفتن تعداد پیامهای در صف بر روی {_serviceName}{Environment.NewLine}تعداد صف {dt.Rows[0]["count"]}";
                await CreateAlertMessage(messageText, false);
            }
        }
    }

    private async Task CreateAlertMessage(string messageText, bool isError)
    {
        _errorCountFlag = false;
        string[] dest = _destinationAddress.Split(',');
        List<MessageDto> listToSend = [];
        List<MessageList> keepData = [];

        listToSend.AddRange(dest.Select(item => new MessageDto()
                                                {
                                                    DataCoding = HasUniCodeCharacter(messageText) ? (int)DataCodings.Ucs2 : (int)DataCodings.Default,
                                                    DestinationAddress = item,
                                                    HasUdh = false,
                                                    Udh = "",
                                                    SourceAddress = _sourceAddress,
                                                    MessageText = messageText + $"{Environment.NewLine}زمان : {DateTime.Now}"
                                                }));

        await SendAlert(listToSend, keepData);
        _errorCount = isError ? 0 : _errorCount;
        _errorCountFlag = true;
    }

    private async Task SendAlert(List<MessageDto> listToSend, List<MessageList> keepData)
    {
        try
        {
            // Try Get Token
            Log.Information("Start Getting token for send alerts");

            if (!_useApiKey)
            {
                GetToken();
            }

            // Try sending Alerts
            HttpClient client = clientFactory.CreateClient();

            if (!_useApiKey)
            {
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + _accessToken);
            }
            else
            {
                client.DefaultRequestHeaders.Add("X-API-Key", _apiKey);
            }

            StringContent content = new(JsonConvert.SerializeObject(listToSend), Encoding.UTF8, MediaTypeNames.Application.Json);

            HttpResponseMessage response = await client.PostAsync(Url.Combine(_smsEndPointBaseAddress, "api/message/send"), content);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                Log.Information("Send alert succeeded");
            }
            else if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (!_useApiKey)
                {
                    Thread.Sleep(1000);
                    await ReSendAlert(listToSend, keepData);
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
            _messageText = $"خطا در سرویس : {_serviceName}{Environment.NewLine}{e.Message}";
            Log.Error(e.Message);
        }
    }

    private async Task ReSendAlert(List<MessageDto> listToSend, List<MessageList> keepData)
    {
        await SendAlert(listToSend, keepData);
    }

    private void UpdateDelivery()
    {
        _saveDlrFlag = false;

        try
        {
            if (!DlrEntranceQueue.IsEmpty)
            {
                Stopwatch saveTimer = new();
                saveTimer.Start();

                List<DlrDto> initialList = [];

                Stopwatch sw1 = new();
                sw1.Start();

                while (!DlrEntranceQueue.IsEmpty && initialList.Count < _batchSize)
                {
                    DlrEntranceQueue.TryDequeue(out DlrDto? outboxDto);

                    if (outboxDto is not null)
                    {
                        initialList.Add(outboxDto);
                    }
                }

                sw1.Stop();

                if (initialList.Any())
                {
                    CurrentDeliverySaveQueues = initialList;
                    List<UpdateDBModel> updateList = [];
                    Stopwatch sw2 = new();
                    Stopwatch sw3 = new();
                    sw2.Start();
                    updateList.AddRange(initialList.Select(dto => new UpdateDBModel { Status = dto.Status, TrackingCode = dto.MessageId, DeliveredAt = Convert.ToDateTime(dto.DateTime).ToString("yyyy-MM-dd HH:mm:ss") }));
                    sw2.Stop();
                    sw3.Start();
                    UpdateDbForDlr(updateList, initialList);
                    sw3.Stop();
                    Log.Information($"DLR - Read from Queue time: {sw1.ElapsedMilliseconds}\t Create update list: {sw2.ElapsedMilliseconds}\t Update list count: {updateList.Count}\t update time: {sw3.ElapsedMilliseconds}");
                }
            }
        }
        catch (Exception e)
        {
            _errorCount++;
            _messageText = $"خطا در سرویس : {_serviceName}{Environment.NewLine}{e.Message}";
            Log.Error($"Error in UpdateDelivery: {e.Message}");
        }

        _saveDlrFlag = true;
    }

    private async void UpdateDbForDlr(List<UpdateDBModel> updateList, List<DlrDto> initialList)
    {
        switch (_dbProvider)
        {
            case "SQL":
            {
                await using SqlConnection cn = new(_connectionString);

                try
                {
                    string cmm = string.Empty;
                    cn.Open();
                    string command = _updateQueryForDelivery;
                    cmm = updateList.Select(item => string.Format(command, (int)item.Status, item.DeliveredAt, item.TrackingCode)).Aggregate(cmm, (current, newCommand) => current + newCommand);
                    
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
                    _messageText = $"خطا در سرویس : {_serviceName}{Environment.NewLine}{e.Message}";

                    foreach (DlrDto item in initialList)
                    {
                        DlrEntranceQueue.Enqueue(item);
                    }

                    Log.Error($"Error in Update. Error is: {e.Message}");
                }

                break;
            }

            case "MySQL":
            {
                await using MySqlConnection cn = new(_connectionString);

                try
                {
                    string cmm = string.Empty;
                    cn.Open();
                    string command = _updateQueryForDelivery;
                    cmm = updateList.Select(item => string.Format(command, item.Status, item.DeliveredAt, item.TrackingCode)).Aggregate(cmm, (current, newCommand) => current + newCommand);
                    await using MySqlCommand cm = new(cmm, cn) { CommandType = CommandType.Text };
                    await cm.ExecuteNonQueryAsync();
                    await cm.Connection.CloseAsync();
                    await cn.CloseAsync();
                }
                catch (Exception e)
                {
                    await cn.CloseAsync();
                    _errorCount++;
                    _messageText = $"خطا در سرویس : {_serviceName}{Environment.NewLine}{e.Message}";

                    foreach (DlrDto item in initialList)
                    {
                        DlrEntranceQueue.Enqueue(item);
                    }

                    Log.Error($"Error in Update. Error is: {e.Message}");
                }

                break;
            }
        }
    }

    private async void UpdateReceive()
    {
        _saveMoFlag = false;

        try
        {
            if (!MoEntranceQueue.IsEmpty)
            {
                Stopwatch saveTimer = new();
                saveTimer.Start();

                List<MoDto> initialList = [];

                Stopwatch sw1 = new();
                sw1.Start();

                while (!MoEntranceQueue.IsEmpty && initialList.Count < _batchSize)
                {
                    MoEntranceQueue.TryDequeue(out MoDto? inboxDto);

                    if (inboxDto is not null)
                    {
                        initialList.Add(inboxDto);
                    }
                }

                sw1.Stop();

                if (initialList.Any())
                {
                    List<MoDto> list = [];
                    Stopwatch sw2 = new();
                    Stopwatch sw3 = new();
                    sw2.Start();

                    list.AddRange(initialList.Select(dto => new MoDto { DestinationAddress = dto.DestinationAddress, SourceAddress = dto.SourceAddress, MessageText = dto.MessageText, ReceiveDateTime = dto.ReceiveDateTime }));

                    sw2.Stop();
                    sw3.Start();

                    if (list.Any())
                    {
                        await InsertInboxAsync(list);
                        Log.Information($"MO - Read from Queue time: {sw1.ElapsedMilliseconds}\t Create list: {sw2.ElapsedMilliseconds}\t list count: {list.Count}\t MySQL insert time: {sw3.ElapsedMilliseconds}");
                    }

                    sw3.Stop();
                }
            }
        }
        catch (Exception e)
        {
            _errorCount++;
            _messageText = $"خطا در سرویس : {_serviceName}{Environment.NewLine}{e.Message}";
            Log.Error($"Error in UpdateReceive: {e.Message}");
        }

        _saveMoFlag = true;
    }

    private async Task InsertInboxAsync(List<MoDto> list)
    {
        switch (_dbProvider)
        {
            case "SQL":
            {
                await using SqlConnection cn = new(_connectionString);

                try
                {
                    string cmm = string.Empty;
                    cn.Open();

                    string command = _insertQueryForInbox;

                    cmm = list.Select(item => string.Format(command, item.DestinationAddress, item.SourceAddress, Convert.ToDateTime(item.ReceiveDateTime).ToString("yyyy-MM-dd HH:mm:ss"), item.MessageText))
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
                    _messageText = $"خطا در سرویس : {_serviceName}{Environment.NewLine}{e.Message}";
                    Log.Error($"Error in insert into inbound. Error is: {e.Message}");
                }

                break;
            }

            case "MySQL":
            {
                await using MySqlConnection cn = new(_connectionString);

                try
                {
                    string cmm = string.Empty;
                    cn.Open();

                    string command = _insertQueryForInbox;

                    cmm = list.Select(item => string.Format(command, item.DestinationAddress, item.SourceAddress, Convert.ToDateTime(item.ReceiveDateTime).ToString("yyyy-MM-dd HH:mm:ss"), item.MessageText))
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
                    _messageText = $"خطا در سرویس : {_serviceName}{Environment.NewLine}{e.Message}";
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

            switch (_dbProvider)
            {
                case "SQL":
                {
                    await using SqlConnection cn = new(_connectionString);

                    cn.Open();

                    try
                    {
                        await using SqlCommand cm = new(string.Format(_selectQueryForSend, _tps), cn);
                        dt.Load(await cm.ExecuteReaderAsync());
                    }
                    catch (Exception e)
                    {
                        Log.Error($"Error in read db (selectQueryForSend): {e.Message}");
                    }
                    finally
                    {
                        await cn.CloseAsync();
                    }

                    break;
                }

                case "MySQL":
                {
                    await using MySqlConnection cn = new(_connectionString);

                    cn.Open();

                    try
                    {
                        await using MySqlCommand cm = new($"{_selectQueryForSend} {_tps}", cn);
                        dt.Load(await cm.ExecuteReaderAsync());
                    }
                    catch (Exception e)
                    {
	                    Log.Error($"============================= {_selectQueryForSend}");
                        Log.Error($"Error in read db (selectQueryForSend): {e.Message}");
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
                    List<string> updateIds = [];
                    List<MessageToSendDto> dtos = [];
                    int tId = 0;

                    foreach (DataRow dr in dt.Rows)
                    {
                        MessageDto dto = new()
                                         {
                                             SourceAddress = dr["SOURCEADDRESS"].ToString()?.Replace("+", ""),
                                             DestinationAddress = dr["DESTINATIONADDRESS"].ToString()?.Replace("+", ""),
                                             MessageText = dr["MESSAGETEXT"].ToString(),
                                             Udh = dr["ID"].ToString(),
                                             DataCoding = HasUniCodeCharacter(dr["MESSAGETEXT"].ToString()) ? 8 : 0
                                         };

                        list.Add(dto);
                        ids.Add(dr["ID"].ToString());
                        updateIds.Add(dr["ID"].ToString());

                        if (list.Count >= _batchSize)
                        {
                            dtos.Add(new() { MessageDtos = list, IdsList = ids });
                            list = [];
                            ids = [];
                        }
                    }

                    if (list.Count > 0)
                    {
                        dtos.Add(new() { MessageDtos = list, IdsList = ids });
                    }

                    sw2.Stop();

                    
                    Stopwatch waitingForSendStopwatch = Stopwatch.StartNew();

                    string command = string.Format(_updateQueryBeforeSend, string.Join(",", ids));
                    switch (_dbProvider)
                    {
                        case "SQL":
                        {
                            await using SqlConnection cn = new(_connectionString);

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
                            await using MySqlConnection cn = new(_connectionString);

                            cn.Open();
                            await using MySqlCommand cm = new(command, cn) { CommandType = CommandType.Text };
                            await cm.ExecuteNonQueryAsync();
                            await cm.Connection.CloseAsync();
                            await cn.CloseAsync();

                            break;
                        }
                    }

                    waitingForSendStopwatch.Stop();
                    
                    sw4.Start();

                    foreach (MessageToSendDto item in dtos)
                    {
                        await Send(item.MessageDtos, item.IdsList, tId++);
                    }

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
                _messageText = $"خطا در سرویس : {_serviceName}{Environment.NewLine}{ex.Message}";
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

        if (!_useApiKey)
        {
            client.DefaultRequestHeaders.Add("Authorization", "Bearer " + _accessToken);
        }
        else
        {
            client.DefaultRequestHeaders.Add("X-API-Key", _apiKey);
        }

        StringContent content = new(JsonConvert.SerializeObject(listToSend), Encoding.UTF8, MediaTypeNames.Application.Json);

        try
        {
            Stopwatch sw1 = new();
            Stopwatch sw2 = new();
            sw1.Start();
            HttpResponseMessage response = await client.PostAsync(Url.Combine(_smsEndPointBaseAddress, $"api/3/message/send?returnLongId={_returnLongId}"), content);


            if (response.StatusCode == HttpStatusCode.OK)
            {
                ResultApiClass<List<KeyValuePair<string, string>>>? batchIds = JsonConvert.DeserializeObject<ResultApiClass<List<KeyValuePair<string, string>>>>(await response.Content.ReadAsStringAsync());
                sw1.Stop();

                if (ids.Count > 0)
                {
                    try
                    {
                        sw2.Start();
                        string sentDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                        string command = batchIds.Data.Select((d, index) => string.Format(_updateQueryAfterSend,
                                                                                          d.Key.Length > 4 ? _statusForSuccessSend : _statusForFailedSend,
                                                                                          sentDate,
                                                                                          d.Key,
                                                                                          d.Value,
                                                                                          ids[index]))
                                                 .Aggregate("", (current, newComm) => current + newComm);

                        switch (_dbProvider)
                        {
                            case "SQL":
                            {
                                await using SqlConnection cn = new(_connectionString);

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
                                await using MySqlConnection cn = new(_connectionString);

                                cn.Open();
                                await using MySqlCommand cm = new(command, cn) { CommandType = CommandType.Text };
                                await cm.ExecuteNonQueryAsync();
                                await cm.Connection.CloseAsync();
                                await cn.CloseAsync();

                                break;
                            }
                        }

                        sw2.Stop();
                        Log.Information($"TaskId: {tId}\tSend time: {sw1.ElapsedMilliseconds}\t count:{listToSend.Count}\t Update count:{ids.Count}\tUpdate time:{sw2.ElapsedMilliseconds}");
                    }
                    catch (Exception e)
                    {
                        _errorCount++;
                        _messageText = $"خطا در سرویس : {_serviceName}{Environment.NewLine}{e.Message}";
                        Log.Error($"Error in Update: {e.Message} - \n {JsonConvert.SerializeObject(batchIds)}");
                    }
                }
            }
            else if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (!_useApiKey)
                {
                    GetToken();
                    await Task.Delay(1000);
                    await Send(listToSend, ids, tId);
                }
                else
                {
                    await UpdateStatus();
                }
            }
            else
            {
                Log.Information($"status code: {response.StatusCode.ToString()} message: {await response.Content.ReadAsStringAsync()}");
            }

            async ValueTask UpdateStatus()
            {
                switch (_dbProvider)
                {
                    case "SQL":
                    {
                        await using SqlConnection cn = new(_connectionString);

                        cn.Open();
                        sw2.Start();
                        string pattern = _updateQueryAfterFailedSend;
                        string command = ids.Select(id => string.Format(pattern, _statusForFailedSend, id)).Aggregate("", (current, newComm) => current + newComm);
                        await using SqlCommand cm = new(command, cn);
                        cm.CommandType = CommandType.Text;
                        await cm.ExecuteNonQueryAsync();
                        await cm.Connection.CloseAsync();
                        await cn.CloseAsync();

                        break;
                    }

                    case "MySQL":
                    {
                        await using MySqlConnection cn = new(_connectionString);

                        cn.Open();
                        sw2.Start();
                        string pattern = _updateQueryAfterFailedSend;
                        string command = ids.Select(id => string.Format(pattern, _statusForFailedSend, id)).Aggregate("", (current, newComm) => current + newComm);
                        await using MySqlCommand cm = new(command, cn) { CommandType = CommandType.Text };
                        await cm.ExecuteNonQueryAsync();
                        await cm.Connection.CloseAsync();
                        await cn.CloseAsync();

                        break;
                    }
                }

                Log.Information($"status code: {response.StatusCode} message: {await response.Content.ReadAsStringAsync()}");
            }
        }
        catch (Exception e)
        {
            _errorCount++;
            _messageText = $"خطا در سرویس : {_serviceName}{Environment.NewLine}{e.Message}";
            Log.Error(e.Message);
        }
    }

    private async Task GetDelivery()
    {
        _dlrFlag = false;

        try
        {
            DataTable dataTable = new();
            int offset = 0;

            switch (_dbProvider)
            {
                case "SQL":
                {
                    await using SqlConnection cn = new(_connectionString);

                    cn.Open();

                    try
                    {
                        bool hasRow = true;

                        while (hasRow)
                        {
                            await using SqlCommand cm = new(string.Format(_selectQueryForGetDelivery, offset), cn);
                            dataTable.Load(await cm.ExecuteReaderAsync());

                            offset += 900;

                            if (dataTable.Rows.Count > 0)
                            {
                                List<string> ids = dataTable.AsEnumerable().Select(m => m[0].ToString()).ToList()!;
                                HttpResponseMessage response = await SetResult(ids);

                                if (response.StatusCode == HttpStatusCode.Unauthorized)
                                {
                                    if (!_useApiKey)
                                    {
                                        GetToken();
                                        response = await SetResult(ids);
                                    }
                                    else
                                    {
                                        Log.Information($"Error in get delivery: {response.StatusCode}");
                                        return;
                                    }
                                }

                                if (response.StatusCode == HttpStatusCode.OK)
                                {
                                    ResultApiClass<List<DlrStatus>>? resultApi = JsonConvert.DeserializeObject<ResultApiClass<List<DlrStatus>>>(await response.Content.ReadAsStringAsync());
                                    List<DlrStatus> statusList = resultApi.Data;

                                    Log.Information($"statusList: {JsonConvert.SerializeObject(statusList)}");

                                    for (int i = 0; i < dataTable.Rows.Count; i++)
                                    {
                                        if (statusList.Any(s => s.Id == dataTable.Rows[i][0].ToString()))
                                        {
                                            DlrStatus dlrStatus = statusList.First(s => s.Id == dataTable.Rows[i][0].ToString());

                                            foreach (Tuple<int, Enums.DeliveryStatus, DateTime?> dlrStatusPartStatus in dlrStatus.PartStatus)
                                            {
                                                if (dlrStatus.DeliveryStatus != Enums.DeliveryStatus.Sent)
                                                {
                                                    DlrDto model = new()
                                                                   {
                                                                       Status = dlrStatus.DeliveryStatus,
                                                                       DateTime = dlrStatus.DeliveryDate.ToString(),
                                                                       MessageId = dlrStatus.Id,
                                                                       FullDelivery = dlrStatus.PartStatus.All(p => p.Item2 == Enums.DeliveryStatus.Delivered),
                                                                       PartNumber = dlrStatusPartStatus.Item1
                                                                   };
                                                    DlrEntranceQueue.Enqueue(model);
                                                }
                                            }
                                        }
                                    }

                                    Log.Information($"start get dlr. count: {dataTable.Rows.Count} - {DlrEntranceQueue.Count}");
                                }
                                else if (response.StatusCode == HttpStatusCode.NotFound)
                                {
                                    Log.Information($"get dlr not found delivery for ids : {JsonConvert.SerializeObject(ids)}");
                                    hasRow = false;
                                }
                                else
                                {
                                    Log.Information($"get dlr - api call error : {JsonConvert.SerializeObject(response)}");
                                    Log.Information($"get dlr - ids : {JsonConvert.SerializeObject(ids)}");
                                    hasRow = false;
                                }
                            }
                            else
                            {
                                hasRow = false;
                            }
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
                    await using MySqlConnection cn = new(_connectionString);

                    cn.Open();

                    try
                    {
                        bool hasRow = true;

                        while (hasRow)
                        {
                            await using MySqlCommand cm = new($"{_selectQueryForGetDelivery} {offset}", cn);
                            dataTable.Load(await cm.ExecuteReaderAsync());

                            offset += 900;

                            if (dataTable.Rows.Count > 0)
                            {
                                List<string> ids = dataTable.AsEnumerable().Select(m => m[0].ToString()).ToList();
                                HttpResponseMessage response = await SetResult(ids);

                                if (response.StatusCode == HttpStatusCode.Unauthorized)
                                {
                                    if (!_useApiKey)
                                    {
                                        GetToken();
                                        response = await SetResult(ids);
                                    }
                                    else
                                    {
                                        Log.Information($"Error in get delivery: {response.StatusCode}");

                                        return;
                                    }
                                }

                                if (response.StatusCode == HttpStatusCode.OK)
                                {
                                    ResultApiClass<List<DlrStatus>>? resultApi = JsonConvert.DeserializeObject<ResultApiClass<List<DlrStatus>>>(await response.Content.ReadAsStringAsync());
                                    List<DlrStatus> statusList = resultApi.Data;

                                    for (int i = 0; i < dataTable.Rows.Count; i++)
                                    {
                                        if (statusList.Any(s => s.Id == dataTable.Rows[i][0].ToString()))
                                        {
                                            DlrStatus dlrStatus = statusList.First(s => s.Id == dataTable.Rows[i][0].ToString());

                                            foreach (Tuple<int, Enums.DeliveryStatus, DateTime?> dlrStatusPartStatus in dlrStatus.PartStatus)
                                            {
                                                if (dlrStatus.DeliveryStatus != Enums.DeliveryStatus.Sent)
                                                {
                                                    DlrDto model = new()
                                                                   {
                                                                       Status = dlrStatus.DeliveryStatus,
                                                                       DateTime = dlrStatus.DeliveryDate.ToString(),
                                                                       MessageId = dlrStatus.Id,
                                                                       FullDelivery = dlrStatus.PartStatus.All(p =>
                                                                                                                   p.Item2 == Enums.DeliveryStatus.Delivered),
                                                                       PartNumber = dlrStatusPartStatus.Item1
                                                                   };
                                                    DlrEntranceQueue.Enqueue(model);
                                                }
                                            }
                                        }
                                    }

                                    Log.Information($"start get dlr. count: {dataTable.Rows.Count}");
                                }
                                else
                                {
                                    Log.Information($"get dlr - api call error : {JsonConvert.SerializeObject(response)}");
                                    Log.Information($"get dlr - ids : {JsonConvert.SerializeObject(ids)}");
                                }
                            }
                            else
                            {
                                hasRow = false;
                            }
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

                if (!_useApiKey)
                {
                    client.DefaultRequestHeaders.Add("Authorization", "Bearer " + _accessToken);
                }
                else
                {
                    client.DefaultRequestHeaders.Add("X-API-Key", _apiKey);
                }

                StringContent content = new(JsonConvert.SerializeObject(ids), Encoding.UTF8, MediaTypeNames.Application.Json);

                return await client.PostAsync(Url.Combine(_smsEndPointBaseAddress, $"api/message/GetDLR?returnLongId={_returnLongId}"), content);
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

        try
        {
            Log.Information("start get mo");

            HttpClient client = clientFactory.CreateClient();

            if (!_useApiKey)
            {
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + _accessToken);
            }
            else
            {
                client.DefaultRequestHeaders.Add("X-API-Key", _apiKey);
            }

            HttpResponseMessage response = await client.GetAsync(Url.Combine(_smsEndPointBaseAddress, "api/message/GetMO?returnId=true"));

            if (response.StatusCode != HttpStatusCode.OK)
            {
                if (!_useApiKey)
                {
                    GetToken();
                }

                _moFlag = true;
                Log.Information($"get mo push response status :{response.StatusCode}");

                return;
            }
            
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _moFlag = true;
                Log.Information("get mo not found item");
                return;
            }

            ResultApiClass<List<MoDto>>? moStatus = JsonConvert.DeserializeObject<ResultApiClass<List<MoDto>>>(await response.Content.ReadAsStringAsync());

            List<MoDto> inboxSaveQueueDtos = moStatus.Data.Select(mo => new MoDto
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
                foreach (MoDto inbox in inboxSaveQueueDtos)
                {
                    MoEntranceQueue.Enqueue(inbox);
                }

                Log.Information($"get mo count: {inboxSaveQueueDtos.Count}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"error get mo :{ex.Message}");
        }

        _moFlag = true;
    }

    private static string MapStatus(string statusCode)
    {
        return statusCode switch
               {
                   "0" => "Send_Error",
                   "-1" => "CreditNotEnough",
                   "-2" => "Server_Error",
                   "-3" => "DeActive_Account",
                   "-4" => "Expired_Account",
                   "-5" => "Invalid_UserOrPassword",
                   "-6" => "Auth_Failed",
                   "-7" => "Server_Busy",
                   "-8" => "Number_At_BackList",
                   "-9" => "Limited_SendDay",
                   "-10" => "Limited_Volume",
                   "-11" => "Invalid_SenderID",
                   "-12" => "Invalid_Receiver",
                   "-13" => "Invalid_Dest_Network",
                   "-14" => "Unreachable_Network",
                   "-15" => "DeActive_SenderID",
                   "-16" => "Invalid_SenderID_Format",
                   "-17" => "Tariff_NotFound",
                   "-18" => "Invalid_IpAddress",
                   "-19" => "Invalid_Pattern",
                   "-20" => "Expired_SenderID",
                   "-21" => "Message_Contains_Link",
                   "-22" => "Invalid_Port",
                   "-23" => "Message_TooLong",
                   "-24" => "FilterWord",
                   "-25" => "Invalid_Reference_Number_Type",
                   "-26" => "Invalid_TargetUDH",
                   "-27" => "Limited_InSend_Month",
                   "-28" => "DataCoding_NotAllowed",
                   "-29" => "NotFound_Route",
                   "-30" => "Message_Contains_Scripts",
                   "-31" => "SettingNotFound",
                   "-100" => "None",
                   _ => "Send_Error"
               };
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        WriteToFile();
        _cTs.Cancel();
        Task.WaitAll();
        Log.Information($"Stop service at: {DateTime.Now}");
        Log.Information(_serviceName, Program.LogConfig.LogFileAddressDirectory);

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