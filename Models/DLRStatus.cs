namespace Arad.SMS.Core.MySqlReader.Models;

public class DlrStatus
{
    public string Id { get; set; }

    public List<Tuple<int, Enums.DeliveryStatus, DateTime?>> PartStatus { get; set; }

    public Enums.DeliveryStatus DeliveryStatus { get; set; }

    public DateTime? DeliveryDate { get; set; }

    public string Udh { get; set; }
}