using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using SIPSorcery.Sys;

namespace SIPSorcery.SIP.App
{
    class SIPReferClientUserAgent: ISIPReferClientUserAgent
    {
        private static string m_userAgent = SIPConstants.SIP_USERAGENT_STRING;
        private static readonly int m_defaultSIPPort = SIPConstants.DEFAULT_SIP_PORT;

        private const int DNS_LOOKUP_TIMEOUT = 5000;
        private const char OUTBOUNDPROXY_AS_ROUTESET_CHAR = '<';    // If this character exists in the call descriptor OutboundProxy setting it gets treated as a Route set.

        private SIPDialogue m_sipDialogue;
        private SIPNonInviteTransaction m_serverTransaction;
        private SIPCallDescriptor m_sipCallDescriptor;
        private int CSeq = 0;
        private string CallId { get; set; }
        private SIPRouteSet RouteSet { get; set; }

        private SIPEndPoint m_serverEndPoint { get; set; }
        private SIPEndPoint m_localSIPEndPoint { get; set; }
        public string Owner;
        public string AdminMemberId;

        public SIPNonInviteTransaction ServerTransaction
        {
            get { return m_serverTransaction; }
            set { m_serverTransaction = value; }
        }
        public SIPDialogue SIPDialogue
        {
            get { return m_sipDialogue; }
            set { m_sipDialogue = value; }
        }
        public SIPCallDescriptor CallDescriptor
        {
            get { return m_sipCallDescriptor; }
            set { m_sipCallDescriptor = value; }
        }
        public bool IsUACAnswered;
        private SIPTransport m_sipTransport { get; set; }
        private SIPEndPoint m_outboundProxy { get; set; }
        private SIPMonitorLogDelegate Log_External { get; set; }

        public event SIPReferAcceptedDelegate ReferAccepted;
        public event SIPReferDeniedDelegate ReferDenied;
        public event SIPReferFailedDelegate ReferFailed;

        public event SIPReferStateChangedDelegate ReferStateChanged;

        public SIPReferClientUserAgent(
            SIPTransport sipTransport,
            SIPEndPoint outboundProxy,
            string owner,
            string adminMemberId,
            SIPMonitorLogDelegate logDelegate)
        {
            m_sipTransport = sipTransport;
            m_outboundProxy = (outboundProxy != null) ? SIPEndPoint.ParseSIPEndPoint(outboundProxy.ToString()) : null;
            Owner = owner;
            AdminMemberId = adminMemberId;
            Log_External = logDelegate;

            // If external logging is not required assign an empty handler to stop null reference exceptions.
            if (Log_External == null)
            {
                Log_External = (e) => { };
            }
        }

        public void ReferOutOfDialog(SIPURI fromUri, SIPURI toUri, SIPURI referToUri, ReplacesCallDescriptor sipReplacesCallDescriptor)
        {
            try
            {
                m_sipCallDescriptor = new SIPCallDescriptor(null,toUri.ToString(),fromUri.ToString(),null,null);
                m_sipCallDescriptor.Gruu = toUri.Parameters.Get("gr");
                m_sipCallDescriptor.ReplacesCall = sipReplacesCallDescriptor;

                SIPURI callURI = SIPURI.ParseSIPURI(m_sipCallDescriptor.Uri);

                    // If the outbound proxy is a loopback address, as it will normally be for local deployments, then it cannot be overriden.
                    if (m_outboundProxy != null && IPAddress.IsLoopback(m_outboundProxy.Address))
                    {
                        m_serverEndPoint = m_outboundProxy;
                    }
                    else if (!m_sipCallDescriptor.ProxySendFrom.IsNullOrBlank())
                    {
                        // If the binding has a specific proxy end point sent then the request needs to be forwarded to the proxy's default end point for it to take care of.
                        SIPEndPoint outboundProxyEndPoint = SIPEndPoint.ParseSIPEndPoint(m_sipCallDescriptor.ProxySendFrom);
                        m_outboundProxy = new SIPEndPoint(SIPProtocolsEnum.udp, new IPEndPoint(outboundProxyEndPoint.Address, m_defaultSIPPort));
                        m_serverEndPoint = m_outboundProxy;
                        Log_External(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.ReferUserAgentClient, SIPMonitorEventTypesEnum.Refer, "SIPReferClientUserAgent refer request using alternate outbound proxy of " + m_outboundProxy + ".", Owner));
                    }
                    else if (m_outboundProxy != null)
                    {
                        // Using the system outbound proxy only, no additional user routing requirements.
                        m_serverEndPoint = m_outboundProxy;
                    }

                    // A custom route set may have been specified for the call.
                    if (m_sipCallDescriptor.RouteSet != null && m_sipCallDescriptor.RouteSet.IndexOf(OUTBOUNDPROXY_AS_ROUTESET_CHAR) != -1)
                    {
                        try
                        {
                            RouteSet = new SIPRouteSet();
                            RouteSet.PushRoute(new SIPRoute(m_sipCallDescriptor.RouteSet, true));
                        }
                        catch
                        {
                            Log_External(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.ReferUserAgentClient, SIPMonitorEventTypesEnum.Refer, "Error an outbound proxy value was not recognised in SIPReferClientUserAgent refer request. " + m_sipCallDescriptor.RouteSet + ".", Owner));
                        }
                    }

                    // No outbound proxy, determine the forward destination based on the SIP request.
                    if (m_serverEndPoint == null)
                    {
                        SIPDNSLookupResult lookupResult = null;

                        if (RouteSet == null || RouteSet.Length == 0)
                        {
                            Log_External(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.ReferUserAgentClient, SIPMonitorEventTypesEnum.Refer, "Attempting to resolve " + callURI.Host + ".", Owner));
                            lookupResult = m_sipTransport.GetURIEndPoint(callURI, false);
                        }
                        else
                        {
                            Log_External(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.ReferUserAgentClient, SIPMonitorEventTypesEnum.Refer, "Route set for refer request " + RouteSet.ToString() + ".", Owner));
                            lookupResult = m_sipTransport.GetURIEndPoint(RouteSet.TopRoute.URI, false);
                        }

                        if (lookupResult.LookupError != null)
                        {
                            Log_External(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.ReferUserAgentClient, SIPMonitorEventTypesEnum.Refer, "DNS error resolving " + callURI.Host + ", " + lookupResult.LookupError + ". Refer request cannot proceed.", Owner));
                        }
                        else
                        {
                            m_serverEndPoint = lookupResult.GetSIPEndPoint();
                        }
                    }

                    if (m_serverEndPoint != null)
                    {
                        Log_External(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.ReferUserAgentClient, SIPMonitorEventTypesEnum.Refer, "Switching to " + SIPURI.ParseSIPURI(m_sipCallDescriptor.Uri).CanonicalAddress + " via " + m_serverEndPoint + ".", Owner));

                        m_localSIPEndPoint = m_sipTransport.GetDefaultSIPEndPoint(m_serverEndPoint);
                        if (m_localSIPEndPoint == null)
                        {
                            throw new ApplicationException("The refer request could not locate an appropriate SIP transport channel for protocol " + callURI.Protocol + ".");
                        }

                        SIPRequest referRequest = GetReferRequest(m_localSIPEndPoint,referToUri);

                        // Now that we have a destination socket create a new UAC transaction for forwarded leg of the call.
                        m_serverTransaction = m_sipTransport.CreateNonInviteTransaction(referRequest, m_serverEndPoint, m_localSIPEndPoint, m_outboundProxy);

                        m_serverTransaction.NonInviteTransactionFinalResponseReceived += m_serverTransaction_NonInviteTransactionFinalResponseReceived;
                        m_serverTransaction.NonInviteTransactionTimedOut += m_serverTransaction_NonInviteTransactionTimedOut;
                        m_serverTransaction.TransactionTraceMessage += TransactionTraceMessage;

                        m_serverTransaction.SendRequest(m_serverTransaction.TransactionRequest);

                    }
                    else
                    {
                        if (RouteSet == null || RouteSet.Length == 0)
                        {
                            Log_External(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.ReferUserAgentClient, SIPMonitorEventTypesEnum.Refer, "Forward leg failed, could not resolve URI host " + callURI.Host, Owner));
                            FireReferFailed(this, "unresolvable destination " + callURI.Host);
                        }
                        else
                        {
                            Log_External(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.ReferUserAgentClient, SIPMonitorEventTypesEnum.Refer, "Forward leg failed, could not resolve top Route host " + RouteSet.TopRoute.Host, Owner));
                            FireReferFailed(this, "unresolvable destination " + RouteSet.TopRoute.Host);
                        }
                    }
            }
            catch (ApplicationException appExcp)
            {
                FireReferFailed(this, appExcp.Message);
            }
            catch (Exception excp)
            {
                Log_External(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.ReferUserAgentClient, SIPMonitorEventTypesEnum.Refer, "Exception UserAgentClient Call. " + excp.Message, Owner));
                FireReferFailed(this, excp.Message);
            }
        }

        void m_serverTransaction_NonInviteTransactionTimedOut(SIPTransaction sipTransaction)
        {
            FireReferFailed(this, "SIP timeout for REFER message to " + sipTransaction.TransactionRequestURI.ToString());
        }

        void m_serverTransaction_NonInviteTransactionFinalResponseReceived(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPTransaction sipTransaction, SIPResponse sipResponse)
        {
            if (sipResponse.Status == SIPResponseStatusCodesEnum.Accepted)
            {
                FireReferAccepted();
            }
            else if (sipResponse.Status == SIPResponseStatusCodesEnum.Decline)
            {
                FireReferDenied();
            }
            else if (sipResponse.Status == SIPResponseStatusCodesEnum.MethodNotAllowed)
            {
                FireReferDenied();
            }
        }

        private void FireReferAccepted()
        {
            var handler = ReferAccepted;
            if (handler != null)
            {
                handler(this);
            }
        }

        private void FireReferDenied()
        {
            var handler = ReferDenied;
            if (handler != null)
            {
                handler(this);
            }
        }

        private void FireReferFailed(SIPReferClientUserAgent ruac, string errorMessage)
        {
            var handler = ReferFailed;
            if (handler != null)
            {
                handler(ruac, errorMessage);
            }
        }


        private SIPRequest GetReferRequest(SIPEndPoint localSIPEndPoint, SIPURI referTo)
        {
            SIPRequest referRequest = new SIPRequest(SIPMethodsEnum.REFER, m_sipCallDescriptor.Uri);
            SIPFromHeader referFromHeader = SIPFromHeader.ParseFromHeader(m_sipCallDescriptor.From);
            SIPToHeader referToHeader = SIPToHeader.ParseToHeader(m_sipCallDescriptor.Uri);
            
            int cseq = ++CSeq;
            CallId = CallProperties.CreateNewCallId();

            SIPHeader referHeader = new SIPHeader(referFromHeader, referToHeader, cseq, CallId);
            referHeader.CSeqMethod = SIPMethodsEnum.REFER;
            referRequest.Header = referHeader;
            referRequest.Header.ReferTo = referTo.ToString();
            referRequest.Header.Routes = RouteSet;
            referRequest.Header.ProxySendFrom = m_sipCallDescriptor.ProxySendFrom;

            SIPViaHeader viaHeader = new SIPViaHeader(localSIPEndPoint, CallProperties.CreateBranchId());
            referRequest.Header.Vias.PushViaHeader(viaHeader);

            return referRequest;
        }

        private void TransactionTraceMessage(SIPTransaction sipTransaction, string message)
        {
            Log_External(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.ReferUserAgentClient, SIPMonitorEventTypesEnum.SIPTransaction, message, Owner));
        }
    }
}
