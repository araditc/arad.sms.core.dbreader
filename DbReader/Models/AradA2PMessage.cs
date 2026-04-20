namespace Arad.SMS.Core.WorkerForDownstreamGateway.DbReader.Models;

public class AradA2PMessage
{
    public required string SourceAddress { get; set; }

    public required string DestinationAddress { get; set; }

    public required string MessageText { get; set; }

    public required string MessageId { get; set; }
}