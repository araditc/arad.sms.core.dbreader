namespace Arad.SMS.Core.WorkerForDownstreamGateway.DbReader.Models;

public class ResultApiV4
{
    public string Id { get; set; } = null!;

    public string UpstreamGateway { get; set; } = null!;

    public int Part { get; set; }
}