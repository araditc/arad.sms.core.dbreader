//
//  --------------------------------------------------------------------
//  Copyright (c) 2005-2023 Arad ITC.
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


namespace Arad.SMS.Core.MySqlReader.Models;

/// <summary>
/// Indicates the encoding scheme of the short message.
/// </summary>
[Flags]
public enum DataCodings : byte
{
    /// <summary>
    /// SMSC Default Alphabet (GSM 7 bit) (0x0)
    /// </summary>
    Default = 0x0,
 
    /// <summary>
    /// UCS2 (ISO/IEC-10646) (0x8)
    /// </summary>
    Ucs2 = 0x8
}