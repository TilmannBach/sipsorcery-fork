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
        SIPNonInviteTransaction ServerTransaction { get; }
        SIPDialogue SIPDialogue { get; }
        SIPCallDescriptor CallDescriptor { get; }
        bool IsUACAnswered { get; }

        event SIPReferAcceptedDelegate ReferAccepted;
        event SIPReferDeniedDelegate ReferDenied;

        event SIPReferStateChangedDelegate ReferStateChanged;

        void Accept();
        void Reject(SIPResponseStatusCodesEnum failureStatus, string reasonPhrase, string[] customHeaders);
    }
}