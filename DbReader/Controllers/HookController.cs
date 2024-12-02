using System.Collections.Specialized;
using System.Diagnostics;
using System.Web;

using Arad.SMS.Core.WorkerForDownstreamGateway.DbReader.Models;

using Microsoft.AspNetCore.Mvc;

using Serilog;

namespace Arad.SMS.Core.WorkerForDownstreamGateway.DbReader.Controllers;

public class HookController : Controller
{
    [HttpGet("GetMO")]
    public ActionResult GetMo()
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
            Worker.InsertInboxAsync(inboxSaveQueues);
        }
        catch (Exception ex)
        {
            Log.Error($"GetMo error :{ex.Message}");

            return BadRequest();
        }

        return Ok("Done.");
    }

    [HttpPost("GetDLR")]
    public ActionResult GetDLR([FromBody] List<DeliveryRelayModel> models)
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
                Worker.UpdateDbForDlr(updateList);
                sw3.Stop();
                Log.Information($"DLR - Create update list: {sw2.ElapsedMilliseconds}\t Update list count: {updateList.Count}\t update time: {sw3.ElapsedMilliseconds}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"GetDLRArad error push :{ex.Message}");

            return BadRequest();
        }

        return Ok("Done.");
    }
}