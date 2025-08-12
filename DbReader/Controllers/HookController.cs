using System.Collections.Specialized;
using System.Data.Common;
using System.Diagnostics;
using System.Web;

using Arad.SMS.Core.DbReader.Factory;
using Arad.SMS.Core.DbReader.Models;

using Microsoft.AspNetCore.Mvc;

using Serilog;

namespace Arad.SMS.Core.DbReader.Controllers;

public class HookController(IDbConnectionFactory connectionFactory) : Controller
{
    [HttpGet("GetMO")]
    public async Task<ActionResult> GetMo(CancellationToken token)
    {
        try
        {
            NameValueCollection queryString = HttpUtility.ParseQueryString(Request.QueryString.ToString());
            string? from = queryString["from"];
            string? to = queryString["to"];
            string? text = queryString["text"];

            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(text))
            {
                return BadRequest();
            }

            string dest = $"98{to}";
            dest = dest.StartsWith("9898") ? dest.Substring(2, dest.Length - 2) : dest;

            List<MoDto> inboxSaveQueues =
            [
                new() { MessageText = text, SourceAddress = from, DestinationAddress = dest, ReceiveDateTime = DateTime.Now }
            ];

            await using DbConnection connection = await connectionFactory.CreateOpenConnectionAsync(token);

            string strCommand = string.Empty;
            strCommand = inboxSaveQueues.Select(item => string.Format(RuntimeSettings.InsertQueryForInbox, item.DestinationAddress, item.SourceAddress, item.ReceiveDateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), item.MessageText)).
                                         Aggregate(strCommand, (current, newCommand) => current + newCommand);

            DbCommand command = connectionFactory.CreateCommand(strCommand, connection);
            await command.ExecuteNonQueryAsync(token);
        }
        catch (Exception ex)
        {
            Log.Error("GetMo error :{ExMessage}", ex.Message);

            return BadRequest();
        }

        return Ok("Done.");
    }

    [HttpPost("GetDLR")]
    public async Task<ActionResult> GetDLR([FromBody] List<DeliveryRelayModel> models, CancellationToken token)
    {
        try
        {
            Log.Information("Start GetDLRArad");

            string? userName = models.FirstOrDefault()?.UserName;

            if (string.IsNullOrWhiteSpace(userName))
            {
                return BadRequest();
            }

            List<DlrDto> initialList = [];

            foreach (DeliveryRelayModel item in models)
            {
                Enum.TryParse(item.Status, out DeliveryStatus status);
                initialList.Add(new() { MessageId = item.MessageId, FullDelivery = item.FullDelivery, DateTime = item.DateTime, Mobile = item.Mobile, PartNumber = Convert.ToInt32(item.PartNumber), Status = status });
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

                await using DbConnection connection = await connectionFactory.CreateOpenConnectionAsync(token);

                string strCommand = string.Empty;
                strCommand = updateList.Select(item => string.Format(RuntimeSettings.UpdateQueryForDelivery, (int)item.Status, item.DeliveredAt, item.TrackingCode)).
                                        Aggregate(strCommand, (current, newCommand) => current + newCommand);

                DbCommand command = connectionFactory.CreateCommand(strCommand, connection);
                await command.ExecuteNonQueryAsync(token);

                sw3.Stop();
                Log.Information("DLR - Create update list: {Sw2ElapsedMilliseconds}\t Update list count: {UpdateListCount}\t update time: {Sw3ElapsedMilliseconds}", sw2.ElapsedMilliseconds, updateList.Count, sw3.ElapsedMilliseconds);
            }
        }
        catch (Exception ex)
        {
            Log.Error("GetDLRArad error push :{ExMessage}", ex.Message);

            return BadRequest();
        }

        return Ok("Done.");
    }
}