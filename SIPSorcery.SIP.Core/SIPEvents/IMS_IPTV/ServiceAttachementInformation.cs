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

namespace SIPSorcery.SIP.IPTV
{
    public class ServiceAttachementInformation
    {
        private static readonly string m_pidfXMLNS = SIPEventConsts.PIDF_XML_NAMESPACE_URN;

        public string SSFID;
        public string Description;
        public Uri PullLocation;

        private ServiceAttachementInformation()
        {}

        public ServiceAttachementInformation(string ssfId)
        {
            SSFID = ssfId;
        }

        public ServiceAttachementInformation(string ssfId, string description, Uri pullLocation)
        {
            SSFID = ssfId;
            Description = description;
            PullLocation = pullLocation;
        }

        public static ServiceAttachementInformation Parse(string tupleXMLStr)
        {
            XElement tupleElement = XElement.Parse(tupleXMLStr);
            return Parse(tupleElement);
        }

        public static ServiceAttachementInformation Parse(XElement tupleElement)
        {
            XNamespace ns = m_pidfXMLNS;

            ServiceAttachementInformation tuple = new ServiceAttachementInformation();
            tuple.SSFID = tupleElement.Attribute("ID").Value;
            tuple.Description = (tupleElement.Element("Description") != null) ? tupleElement.Element("Description").Value : null;
            tuple.PullLocation = (tupleElement.Element("Pull") != null) ? new Uri(tupleElement.Element("Pull").Attribute("Location").Value) : null;
            
            return tuple;
        }
    }
}
