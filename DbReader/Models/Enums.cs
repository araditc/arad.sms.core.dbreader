using System.ComponentModel;

namespace Arad.SMS.Core.DbReader.Models;

public enum ApiResponse
{
    [Description("Succeeded")] Succeeded = 100,

    [Description("DatabaseError")] DatabaseError = 101,

    [Description("RepositoryError")] RepositoryError = 102,

    [Description("ModelError")] ModelError = 103,

    [Description("Error connecting to api")]
    ApiConnectionAttemptFailure = 104,

    [Description("Service unavailable at the moment")]
    ServiceUnAvailable = 105,

    [Description("Confirm email failed")] ConfirmEmailFailed = 106,

    [Description("User not found")] UserNotFound = 107,

    [Description("Item already exists")] DuplicateError = 108,

    [Description("Item not found!")] NotFound = 109,

    [Description("Could not json convert the result!")]
    UnableToCreateResultApiError = 110,

    [Description("Error mapping object!")] AutoMapperError = 111,

    [Description("GeneralFailure")] GeneralFailure = 112,

    [Description("Error in 3rd party web service.")]
    WebServiceError = 113,

    [Description("Unable to recieve token from identity provider.")]
    ErrorGettingTokenFromIdp = 114,

    [Description("Can not read appsettings.json")]
    ErrorRetrievingDataFromAppSettings = 115,

    [Description("Headers of request is not correctly set.")]
    HeaderError = 116,

    [Description("Bad request.")] BadRequestError = 117,

    [Description("RangeLimitExceed")] RangeLimitExceedResponse = 118
}

public enum DeliveryStatus
{
    Delivered = 1, //پیامک با موفقیت ارسال و توسط گوشی دریافت شد
    UnDelivered = 2, //به گوشی نرسیده است
    Accepted = 3, //پیامک به اپراتور ارسال شده
    Rejected = 5, //به اپراتور نرسیده
    ErrorInSending = 7, // در ارسال پیامک خطا رخ داده است
    WaitingForSend = 8, //منتظر ارسال
    Sent = 9, //ارسال شده
    NotSent = 10, //ارسال نشده
    Expired = 11, //منقضی
    BlackList = 14, //لیست سیاه
    SmsIsFilter = 15, //متن پیامک فیلتر میباشد
    Deleted = 16, //حذف شده
    Stored = 29, // ذخیره شد
    Unknown = 32,
    Enroute = 33,
    Undeliverable = 34,
    UnreachableNetwork = 36
}