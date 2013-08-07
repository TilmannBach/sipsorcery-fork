// ============================================================================
// FileName: SIPEventPresence.cs
//
// Description:
// Represents the top level XML element on a SIP event presence payload as described in: 
// RFC3856 "A Presence Event Package for the Session Initiation Protocol (SIP)".
//
// Author(s):
// Aaron Clauson
//
// History:
// 23 Mar 2010	Aaron Clauson	Created.
//
// License: 
// This software is licensed under the BSD License http://www.opensource.org/licenses/bsd-license.php
//
// Copyright (c) 2010 Aaron Clauson (aaron@sipsorcery.com), SIPSorcery Ltd, London, UK (www.sipsorcery.com)
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without modification, are permitted provided that 
// the following conditions are met:
//
// Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer. 
// Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following 
// disclaimer in the documentation and/or other materials provided with the distribution. Neither the name of Blue Face Ltd. 
// nor the names of its contributors may be used to endorse or promote products derived from this software without specific 
// prior written permission. 
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, 
// BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. 
// IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, 
// OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, 
// OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, 
// OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE 
// POSSIBILITY OF SUCH DAMAGE.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Resources;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using SIPSorcery.Sys;
using log4net;

#if UNITTEST
using NUnit.Framework;
#endif

namespace SIPSorcery.SIP
{
    /// <summary>
    /// 
    /// </summary>
    /// <remarks>
    /// 
    /// </remarks>
    public class SIPEventIptv : SIPEvent
    {
        private static ILog logger = AppState.logger;

        private static readonly string m_pidfXMLNS = SIPEventConsts.PIDF_XML_NAMESPACE_URN;

        public List<IPTV.ServiceAttachementInformation> SSFs = new List<IPTV.ServiceAttachementInformation>();

        public SIPEventIptv()
        { }

        public override void Load(string serviceAttachementInformationXML)
        {
            try
            {
                XNamespace ns = m_pidfXMLNS;
                XDocument saiDoc = XDocument.Parse(serviceAttachementInformationXML);

                var tupleElements = saiDoc.Root.Elements("SSF"); // ns +
                foreach (XElement tupleElement in tupleElements)
                {
                    SSFs.Add(IPTV.ServiceAttachementInformation.Parse(tupleElement));
                }
            }
            catch (Exception excp)
            {
                logger.Error("Exception SIPEventUaprofile Load. " + excp.Message);
                throw;
            }
        }

        public static SIPEventIptv Parse(string presenceXMLStr)
        {
            SIPEventIptv presenceEvent = new SIPEventIptv();
            presenceEvent.Load(presenceXMLStr);
            return presenceEvent;
        }
    }
}