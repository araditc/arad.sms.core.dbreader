namespace Arad.SMS.Core.MySqlReader.Models;

public class MoDto
{
    public string Id { get; set; }

    public string SourceAddress { get; set; }

    public string DestinationAddress { get; set; }

    public string MessageText { get; set; }

    public DateTime ReceiveDateTime { get; set; }

    public bool IsRead { get; set; }
}