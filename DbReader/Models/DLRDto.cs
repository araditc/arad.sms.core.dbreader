//
//  --------------------------------------------------------------------
//  Copyright (c) 2005-2024 Arad ITC.
//
//  Author : Ammar Heidari <ammar@arad-itc.org>
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

namespace Arad.SMS.Core.DbReader.Models;

public class DlrDto
{
    public DeliveryStatus Status { get; set; }

    public int PartNumber { get; set; }

    public string MessageId { get; set; }

    public string DateTime { get; set; }

    public string Mobile { get; set; }

    public bool FullDelivery { get; set; }
}

public class DeliveryRelayModel
{
    public string Status { get; set; }

    public string PartNumber { get; set; }

    public string MessageId { get; set; }

    public string DateTime { get; set; }

    public string Mobile { get; set; }

    public string UserName { get; set; }

    public bool FullDelivery { get; set; }

    public string SenderId { get; set; }

    public string UDH { get; set; }

    public string ErrorCode { get; set; }
}