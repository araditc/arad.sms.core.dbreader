using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Arad.SMS.Core.WorkerForDownstreamGateway.DbReader.Authentication;

public class ApiKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
                                         ILoggerFactory logger,
                                         UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        try
        {
            if (!Request.Headers.ContainsKey("X-API-Key"))
            {
                return AuthenticateResult.Fail("Authorization header missing.");
            }

            // Get authorization key
            string apiKey = Request.Headers["X-API-Key"].ToString();
            Regex authHeaderRegex = new("(.*)");

            if (!authHeaderRegex.IsMatch(apiKey))
            {
                return AuthenticateResult.Fail("ApiKey not formatted properly.");
            }
            
            if (RuntimeSettings.SendApiKey != apiKey)
            {
                return AuthenticateResult.Fail("The ApiKey is not correct.");
            }

            List<Claim> claims = [new(ClaimTypes.Name, "ApiKeyUser")];

            ClaimsIdentity identity = new(claims, Scheme.Name);
            ClaimsPrincipal principal = new(identity);

            return AuthenticateResult.Success(new(principal, Scheme.Name));
        }
        catch (Exception e)
        {
            return AuthenticateResult.Fail(e.Message);
        }
    }
}