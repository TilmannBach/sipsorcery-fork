using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SIPSorcery.SIP.App
{
    public interface ISIPReferServerUserAgent
    {
        string Owner { get; }
        string AdminMemberId { get; }
        SIPNonInviteTransaction ClientTransaction { get; }
        SIPDialogue SIPDialogue { get; }
        bool IsUASAnswered { get; }
        SIPURI ReferToUri { get; }
        ReplacesCallDescriptor ReplacedCall { get; }
        string NewCallId { get; }

        event SIPReferFailedDelegate ReferFailed;

        event SIPReferServerStateChangedDelegate ReferStateChanged;

        void Accept();
        void Reject(SIPResponseStatusCodesEnum failureStatus, string reasonPhrase, string[] customHeaders);
        void Progress(SIPResponseStatusCodesEnum progressStatus, string reasonPhrase, string[] customHeaders);
    }

    public enum SIPReferStatusCodesEnum
    {
        /// <summary>
        /// REFER is accepted but resulting INVITE request has no state yet.
        /// </summary>
        Accepted,
        /// <summary>
        /// REFER is accepted and resulting dialog is in early state.
        /// </summary>
        Early,
        /// <summary>
        /// REFER is accepted and resulting dialog is not in early state anymore (so confirmed or terminated).
        /// </summary>
        NonEarly,
        /// <summary>
        /// SIPReferServerUserAgent is in a final state, so no more NOTIFY messages will be sent.
        /// </summary>
        Final
    }
}