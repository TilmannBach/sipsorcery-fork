using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SIPSorcery.SIP.App
{
    public class SIPReferServerUserAgent : ISIPReferServerUserAgent
    {

        public string Owner { get; set; }
        public string AdminMemberId { get; set; }

        private SIPTransport m_sipTransport;
        private SIPNonInviteTransaction m_sipTransaction;
        private SIPDialogue m_sipDialogue;
        private SIPMonitorLogDelegate Log_External;
        private SIPReferStatusCodesEnum referState;

        public SIPNonInviteTransaction ClientTransaction
        {
            get { return m_sipTransaction; }
            set { m_sipTransaction = value; }
        }
        public SIPDialogue SIPDialogue
        {
            get { return m_sipDialogue; }
            set { m_sipDialogue = value; }
        }
        public bool IsUASAnswered
        {
            get { return m_sipTransaction != null && m_sipTransaction.TransactionFinalResponse != null; }
        }
        public SIPURI ReferToUri { get; set; }
        public ReplacesCallDescriptor ReplacedCall { get; set; }
        public string NewCallId { get; set; }
        
        public event SIPReferFailedDelegate ReferFailed;
        public event SIPReferServerStateChangedDelegate ReferStateChanged;

        public SIPReferServerUserAgent(SIPTransport sipTransport, SIPMonitorLogDelegate logDelegate, SIPNonInviteTransaction sipTransaction)
        {
            m_sipTransport = sipTransport;
            Log_External = logDelegate;
            m_sipTransaction = sipTransaction;

            m_sipTransaction.TransactionTraceMessage += TransactionTraceMessage;
            m_sipTransaction.NonInviteTransactionTimedOut += ClientTimedOut;

            // If external logging is not required assign an empty handler to stop null reference exceptions.
            if (Log_External == null)
            {
                Log_External = (e) => { };
            }

            var referTo = SIPURI.ParseSIPURI(m_sipTransaction.TransactionRequest.Header.ReferTo);
            var replacesParameter = SIPReplacesParameter.Parse(referTo.Headers.Get("Replaces"));

            ReplacedCall = new ReplacesCallDescriptor();
            ReplacedCall.CallId = replacesParameter.CallID;
            ReplacedCall.FromTag = replacesParameter.FromTag;
            ReplacedCall.ToTag = replacesParameter.ToTag;

            ReferToUri = referTo.CopyOf();
            ReferToUri.Headers.RemoveAll();
        }

        /// <summary>
        /// Accepting a SIP REFER request
        /// </summary>
        public void Accept()
        {
            FireReferStateChanged(SIPReferStatusCodesEnum.Accepted);

            SIPResponse okayResponse = SIPTransport.GetResponse(m_sipTransaction.TransactionRequest, SIPResponseStatusCodesEnum.Accepted, null);
            m_sipTransport.SendResponse(okayResponse);
        }

        /// <summary>
        /// Rejecting a SIP REFER request 
        /// </summary>
        /// <param name="failureStatus"></param>
        /// <param name="reasonPhrase"></param>
        /// <param name="customHeaders"></param>
        public void Reject(SIPResponseStatusCodesEnum failureStatus, string reasonPhrase, string[] customHeaders)
        {
            FireReferStateChanged(SIPReferStatusCodesEnum.Final);

            SIPResponse rejectResponse = SIPTransport.GetResponse(m_sipTransaction.TransactionRequest, failureStatus, reasonPhrase);
            if (customHeaders != null) rejectResponse.Header.UnknownHeaders.AddRange(customHeaders);
            m_sipTransport.SendResponse(rejectResponse);
        }

        /// <summary>
        /// [NOT IMPLEMENTED] Sending SIP NOTIFY messages to inform initiator about status of the REFER-initiated INVITE request.
        /// </summary>
        /// <param name="progressStatus"></param>
        /// <param name="reasonPhrase"></param>
        /// <param name="customHeaders"></param>
        public void Progress(SIPResponseStatusCodesEnum progressStatus, string reasonPhrase, string[] customHeaders)
        {
            throw new NotImplementedException();
        }

        private void FireReferStateChanged(SIPReferStatusCodesEnum statusCode)
        {
            referState = statusCode;

            var handler = ReferStateChanged;
            if(handler != null)
            {
                handler(this, statusCode);
            }

        }

        private void TransactionTraceMessage(SIPTransaction sipTransaction, string message)
        {
            Log_External(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.ReferUserAgentServer, SIPMonitorEventTypesEnum.SIPTransaction, message, null));
        }

        private void ClientTimedOut(SIPTransaction sipTransaction)
        {
            Log_External(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.ReferUserAgentServer, SIPMonitorEventTypesEnum.Refer, "ReferUAS for " + m_sipTransaction.TransactionRequest.URI.ToString() + " timed out in transaction state " + m_sipTransaction.TransactionState + ".", null));
        }
    }
}
