using System.Data;

using Arad.SMS.Core.DbReader.Models;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

using MySql.Data.MySqlClient;

using Oracle.ManagedDataAccess.Client;

using Serilog;

namespace Arad.SMS.Core.DbReader.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = "ApiKey")]
public class MessageController : Controller
{
    [HttpPost("Send")]
    public async Task<IActionResult> Send([FromBody] List<AradA2PMessage> list, CancellationToken token)
    {
        string cmm = string.Empty;

        foreach (AradA2PMessage item in list)
        {
            if (string.IsNullOrWhiteSpace(item.MessageId))
            {
                byte[] gb = Guid.NewGuid().ToByteArray();
                item.MessageId = Math.Abs(BitConverter.ToInt64(gb, 0)).ToString();
            }

            cmm = string.Format(RuntimeSettings.InsertQueryForOutbox, item.SourceAddress, item.SourceAddress,item.DestinationAddress, item.MessageText, DateTime.Now.ToString("s"), item.MessageId);
        }

        switch (RuntimeSettings.DbProvider)
        {
            case "SQL":
            {
                await using SqlConnection cn = new(RuntimeSettings.ConnectionString);

                try
                {
                    await cn.OpenAsync(token);
                    await using SqlCommand cm = new(cmm, cn);
                    cm.CommandType = CommandType.Text;
                    await cm.ExecuteNonQueryAsync(token);
                    cm.Connection.Close();
                    await cn.CloseAsync();
                }
                catch (Exception e)
                {
                    await cn.CloseAsync();
                    Log.Error($"Error in insert into outbound. Error is: {e.Message}");
                    return StatusCode(500);
                }

                break;
            }

            case "MySQL":
            {
                await using MySqlConnection cn = new(RuntimeSettings.ConnectionString);

                try
                {
                    await cn.OpenAsync(token);
                    await using MySqlCommand cm = new(cmm, cn) { CommandType = CommandType.Text };
                    await cm.ExecuteNonQueryAsync(token);
                    await cm.Connection.CloseAsync();
                    await cn.CloseAsync();
                }
                catch (Exception e)
                {
                    await cn.CloseAsync();
                    Log.Error($"Error in insert into outbound. Error is: {e.Message}");
                    return StatusCode(500);
                }

                break;
            }

            case "Oracle":
            {
                await using OracleConnection cn = new(RuntimeSettings.ConnectionString);

                try
                {
                    await cn.OpenAsync(token);
                    await using OracleCommand cm = new(cmm, cn);
                    cm.CommandType = CommandType.Text;
                    await cm.ExecuteNonQueryAsync(token);
                    await cm.Connection.CloseAsync();
                    await cn.CloseAsync();
                }
                catch (Exception e)
                {
                    await cn.CloseAsync();
                    Log.Error($"Error in insert into outbound. Error is: {e.Message}");
                    return StatusCode(500);
                }

                break;
            }
        }

        return Ok(list.Select(a => a.MessageId));
    }

    [HttpPost("GetDelivery")]
    public async Task<IActionResult> GetDelivery([FromBody] List<string> list, CancellationToken token)
    {
        string cmm = string.Format(RuntimeSettings.SelectDeliveryQuery, string.Join(",", list.Select(x => $"'{x}'")));
        DataTable dt = new ();

        switch (RuntimeSettings.DbProvider)
        {
            case "SQL":
            {
                await using SqlConnection cn = new(RuntimeSettings.ConnectionString);

                try
                {
                    await cn.OpenAsync(token);
                    await using SqlCommand cm = new(cmm, cn);
                    cm.CommandType = CommandType.Text;
                    dt.Load(await cm.ExecuteReaderAsync(token));
                    cm.Connection.Close();
                    await cn.CloseAsync();
                }
                catch (Exception e)
                {
                    await cn.CloseAsync();
                    Log.Error($"Error in select form outbound. Error is: {e.Message}");
                    return StatusCode(500);
                }

                break;
            }

            case "MySQL":
            {
                await using MySqlConnection cn = new(RuntimeSettings.ConnectionString);

                try
                {
                    await cn.OpenAsync(token);
                    await using MySqlCommand cm = new(cmm, cn) { CommandType = CommandType.Text };
                    dt.Load(await cm.ExecuteReaderAsync(token));
                    await cm.Connection.CloseAsync();
                    await cn.CloseAsync();
                }
                catch (Exception e)
                {
                    await cn.CloseAsync();
                    Log.Error($"Error in select form outbound. Error is: {e.Message}");
                    return StatusCode(500);
                }

                break;
            }

            case "Oracle":
            {
                await using OracleConnection cn = new(RuntimeSettings.ConnectionString);

                try
                {
                    await cn.OpenAsync(token);
                    await using OracleCommand cm = new(cmm, cn);
                    cm.CommandType = CommandType.Text;
                    dt.Load(await cm.ExecuteReaderAsync(token));
                    await cm.Connection.CloseAsync();
                    await cn.CloseAsync();
                }
                catch (Exception e)
                {
                    await cn.CloseAsync();
                    Log.Error($"Error in select form outbound. Error is: {e.Message}");
                    return StatusCode(500);
                }

                break;
            }
        }

        return Ok(dt.AsEnumerable().Select(row => row["status"].ToString()));
    }
}