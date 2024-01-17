//
//  --------------------------------------------------------------------
//  Copyright (c) 2005-2021 Arad ITC.
//
//  Author : Davood Ghashghaei <ghashghaei@arad-itc.org>
//  Licensed under the Apache License, Version 2.0 (the "License")
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0 
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
//  --------------------------------------------------------------------

using System.ComponentModel;

namespace Arad.SMS.Core.MySqlReader.Models;

public static class Enums
{

    public enum ApiResponse
    {

        [Description("Succeeded")]
        Succeeded = 100,

        [Description("DatabaseError")]
        DatabaseError = 101,

        [Description("RepositoryError")]
        RepositoryError = 102,

        [Description("ModelError")]
        ModelError = 103,

        [Description("Error connecting to api")]
        ApiConnectionAttemptFailure = 104,

        [Description("Service unavailable at the moment")]
        ServiceUnAvailable = 105,

        [Description("Confirm email failed")]
        ConfirmEmailFailed = 106,


        [Description("User not found")]
        UserNotFound = 107,

        [Description("Item already exists")]
        DuplicateError = 108,

        [Description("Item not found!")]
        NotFound = 109,

        [Description("Could not json convert the result!")]
        UnableToCreateResultApiError = 110,

        [Description("Error mapping object!")]
        AutoMapperError = 111,

        [Description("GeneralFailure")]
        GeneralFailure = 112,

        [Description("Error in 3rd party web service.")]
        WebServiceError = 113,

        [Description("Unable to recieve token from identity provider.")]
        ErrorGettingTokenFromIdp = 114,

        [Description("Can not read appsettings.json")]
        ErrorRetrievingDataFromAppSettings = 115,

        [Description("Headers of request is not correctly set.")]
        HeaderError = 116,
           
        [Description("Bad request.")]
        BadRequestError = 117,
            
        [Description("RangeLimitExceed")]
        RangeLimitExceedResponse = 118
    }

 
    public enum DeliveryStatus
    {
        Delivered = 1, 
        UnDelivered = 2, 
        Accepted = 3, 
        ReceivedByUpstream = 4, 
        Rejected = 5,
        NotReceiveByServer = 6, 
        ErrorInSending = 7, 
        WaitingForSend = 8,
        Sent = 9,
        NotSent = 10,
        Expired = 11,
        IsSending = 12,
        IsCanceled = 13,
        BlackList = 14, 
        SmsIsFilter = 15, 
        Deleted = 16,
        WaitingForConfirmation = 17,
        NotEnoughBalance = 18, 
        IsPreparing = 19, 
        IsPreparedForSending = 20,
        AccessDenied = 21, 
        TextIsEmpty = 22, 
        InvalidInputFormat = 23, 
        InvalidUserOrPassword = 24, 
        InvalidUsedMethod = 25, 
        InvalidSender = 26, 
        InvalidMobile = 27, 
        InvalidReception = 28,
        Stored = 29, 
        BlackListTable = 30, 
        GetDeliveryStatus = 31,
        Unknown = 32,
        Enroute = 33,
        Undeliverable = 34,
        MessageQueueFull = 35,
        UnreachableNetwork = 36
    }
}