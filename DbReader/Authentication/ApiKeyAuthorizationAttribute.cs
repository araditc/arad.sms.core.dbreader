using Microsoft.AspNetCore.Authorization;

namespace Arad.SMS.Core.DbReader.Authentication;

public class ApiKeyAuthorizationAttribute : AuthorizeAttribute
{
    public ApiKeyAuthorizationAttribute() => Policy = "ApiKeyAuthentication";
}