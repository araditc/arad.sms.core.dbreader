using Microsoft.AspNetCore.Authorization;

namespace Arad.SMS.Core.WorkerForDownstreamGateway.DbReader.Authentication;

public class ApiKeyAuthorizationAttribute : AuthorizeAttribute
{
    public ApiKeyAuthorizationAttribute() => Policy = "ApiKeyAuthentication";
}