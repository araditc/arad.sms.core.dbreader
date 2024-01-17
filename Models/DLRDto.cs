namespace Arad.SMS.Core.MySqlReader.Models;

public class DlrDto
{
    public string Status { get; set; }
    public string PartNumber { get; set; }
    public string MessageId { get; set; }
    public string DateTime { get; set; }
    public string Mobile { get; set; }
    public bool FullDelivery { get; set; }
}