namespace Arad.SMS.Core.MySqlReader.Models;

public class MessageList
{
    public List<KeyValuePair<string,int>> Ids { get; set; }
    public MessageDto MessageDto { get; set; }
}

public class MessageDto
{
    public string Udh { get; set; }
    public string MessageText { get; set; }
    public string SourceAddress { get; set; }
    public string DestinationAddress { get; set; }
    public int DataCoding { get; set; }
    public bool HasUdh { get; set; }
}