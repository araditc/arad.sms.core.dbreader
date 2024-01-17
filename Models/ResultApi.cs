namespace Arad.SMS.Core.MySqlReader.Models;

public class ResultApiClass<TClass> where TClass : class
{
    public string Message { get; set; }
    public bool Succeeded { get; set; }
    public TClass Data { get; set; }
    public Enums.ApiResponse ResultCode { get; set; }
}