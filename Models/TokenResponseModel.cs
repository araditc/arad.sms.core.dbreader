namespace Arad.SMS.Core.MySqlReader.Models;

public class TokenResponseModel
{
    public string AccessToken { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string Scope { get; set; }
}