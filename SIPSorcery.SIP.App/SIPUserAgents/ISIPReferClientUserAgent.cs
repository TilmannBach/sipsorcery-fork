using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SIPSorcery.SIP.App
{

    public interface ISIPReferClientUserAgent
    {
        string Owner { get; }
        string AdminMemberId { get; }
        SIPNonInviteTransaction ServerTransaction { get; }
        SIPDialogue SIPDialogue { get; }
        SIPCallDescriptor CallDescriptor { get; }

        event SIPReferAcceptedDelegate ReferAccepted;
        event SIPReferDeniedDelegate ReferDenied;

        event SIPReferStateChangedDelegate ReferStateChanged;

        void ReferOutOfDialog(SIPURI fromUri, SIPURI toUri, SIPURI referToUri, ReplacesCallDescriptor sipReplacesCallDescriptor);
    }
}