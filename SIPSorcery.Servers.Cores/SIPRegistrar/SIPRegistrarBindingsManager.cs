﻿// ============================================================================
// FileName: SIPRegistrarBindingsManager.cs
//
// Description:
// Manages the storing, updating and retrieval of bindings for a SIP Registrar.
//
// Author(s):
// Aaron Clauson
//
// History:
// 21 May 2009	Aaron Clauson	Created.
//
// License: 
// This software is licensed under the BSD License http://www.opensource.org/licenses/bsd-license.php
//
// Copyright (c) 2008 Aaron Clauson (aaronc@blueface.ie), Blue Face Ltd, Dublin, Ireland (www.blueface.ie)
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
using System.Collections;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Data.Linq;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Transactions;
using SIPSorcery.Persistence;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Sys;
using log4net;

namespace SIPSorcery.Servers
{
    public class SIPRegistrarBindingsManager
    {
        private class NATKeepAliveJob
        {
            public SIPEndPoint ProxyEndPoint;   // The proxy socket the NAT keep-alive packet should be sent from;
            public SIPEndPoint RemoteEndPoint;  // Where the NAT keep-alive packet should be sent by the proxy.
            public DateTime? NextSendTime;
            public DateTime EndTime;
            public string Owner;
            public bool Cancel;                 // If set to true indicates the NAT keep alive job should be removed.

            public NATKeepAliveJob(SIPEndPoint proxyEndPoint, SIPEndPoint remoteEndPoint, DateTime endTime, string owner)
            {
                ProxyEndPoint = proxyEndPoint;
                RemoteEndPoint = remoteEndPoint;
                NextSendTime = null;
                EndTime = endTime;
                Owner = owner;
                Cancel = false;
            }

            public void CancelJob()
            {
                Cancel = true;
            }

            public void Update(SIPEndPoint proxyEndPoint, DateTime endTime)
            {
                ProxyEndPoint = proxyEndPoint;
                NextSendTime = null;
                EndTime = endTime;
            }
        }

        private const string EXPIRE_BINDINGS_THREAD_NAME = "sipregistrar-expirebindings";
        private const string SEND_KEEPALIVES_THREAD_NAME = "sipregistrar-natkeepalives";
        private const int CHECK_REGEXPIRY_DURATION = 1000;            // Period at which to check for expired bindings.
        public const int NATKEEPALIVE_DEFAULTSEND_INTERVAL = 10;
        private const int MAX_USERAGENT_LENGTH = 128;
        public const int MINIMUM_EXPIRY_SECONDS = 120;
        private const int DEFAULT_BINDINGS_PER_USER = 1;              // The default maixmim number of bindings that will be allowed for each unique SIP account.
        private const int REMOVE_EXPIRED_BINDINGS_INTERVAL = 3000;    // The interval in seconds at which to check for and remove expired bindings.
        private const int SEND_NATKEEPALIVES_INTERVAL = 5000;
        private const int BINDING_EXPIRY_GRACE_PERIOD = 10;

        private string m_sipRegisterRemoveAll = SIPConstants.SIP_REGISTER_REMOVEALL;
        private string m_sipExpiresParameterKey = SIPContactHeader.EXPIRES_PARAMETER_KEY;

        private static ILog logger = AppState.GetLogger("sipregistrar");

        private SIPMonitorLogDelegate SIPMonitorEventLog_External;
        private SendNATKeepAliveDelegate SendNATKeepAlive_External;

        private SIPAssetPersistor<SIPRegistrarBinding> m_bindingsPersistor;
        private SIPUserAgentConfigurationManager m_userAgentConfigs;
        private Dictionary<string, NATKeepAliveJob> m_natKeepAliveJobs = new Dictionary<string, NATKeepAliveJob>();
        private int m_maxBindingsPerAccount;
        private bool m_stop;

        public SIPRegistrarBindingsManager(
            SIPMonitorLogDelegate sipMonitorEventLog,
            SIPAssetPersistor<SIPRegistrarBinding> bindingsPersistor,
            SendNATKeepAliveDelegate sendNATKeepAlive,
            int maxBindingsPerAccount,
            SIPUserAgentConfigurationManager userAgentConfigs)
        {
            SIPMonitorEventLog_External = sipMonitorEventLog;
            m_bindingsPersistor = bindingsPersistor;
            SendNATKeepAlive_External = sendNATKeepAlive;
            m_maxBindingsPerAccount = (maxBindingsPerAccount != 0) ? maxBindingsPerAccount : DEFAULT_BINDINGS_PER_USER;
            m_userAgentConfigs = userAgentConfigs;
        }

        public void Start()
        {
            ThreadPool.QueueUserWorkItem(delegate { ExpireBindings(); });
            ThreadPool.QueueUserWorkItem(delegate { SendNATKeepAlives(); });
        }

        public void Stop()
        {
            m_stop = true;
        }

        private void ExpireBindings()
        {
            try
            {
                Thread.CurrentThread.Name = EXPIRE_BINDINGS_THREAD_NAME;

                while (!m_stop)
                {
                    try
                    {
                        DateTimeOffset expiryTime = DateTimeOffset.UtcNow.AddSeconds(BINDING_EXPIRY_GRACE_PERIOD * -1);
                        SIPRegistrarBinding expiredBinding = GetNextExpiredBinding(expiryTime);

                        while (expiredBinding != null)
                        {
                            FireSIPMonitorLogEvent(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.Registrar, SIPMonitorEventTypesEnum.BindingExpired, "Expired binding deleted for " + expiredBinding.SIPAccountName + " and " + expiredBinding.MangledContactURI + ", last register " +
                                expiredBinding.LastUpdate.ToString("HH:mm:ss") + ", expiry " + expiredBinding.Expiry + ", expiry time " + expiredBinding.ExpiryTime.ToString("HH:mm:ss") + ", now " + expiryTime.ToString("HH:mm:ss") + ".", expiredBinding.Owner));

                            FireSIPMonitorLogEvent(new SIPMonitorMachineEvent(SIPMonitorMachineEventTypesEnum.SIPRegistrarBindingRemoval, expiredBinding.Owner, expiredBinding.SIPAccountId.ToString(), SIPURI.ParseSIPURIRelaxed(expiredBinding.SIPAccountName + "@sipsorcery.com")));

                            lock (m_natKeepAliveJobs)
                            {
                                if (m_natKeepAliveJobs.ContainsKey(expiredBinding.RemoteSIPSocket))
                                {
                                    m_natKeepAliveJobs[expiredBinding.RemoteSIPSocket].CancelJob();
                                }
                            }

                            expiryTime = DateTimeOffset.UtcNow.AddSeconds(BINDING_EXPIRY_GRACE_PERIOD * -1);
                            expiredBinding = GetNextExpiredBinding(expiryTime);
                        }
                    }
                    catch (Exception expireExcp)
                    {
                        logger.Error("Exception ExpireBindings Delete. " + expireExcp.Message);
                    }

                    Thread.Sleep(REMOVE_EXPIRED_BINDINGS_INTERVAL);
                }
            }
            catch (Exception excp)
            {
                logger.Error("Exception ExpireBindings. " + excp.Message);
            }
            finally
            {
                logger.Warn("Thread " + EXPIRE_BINDINGS_THREAD_NAME + " stopped!");
            }
        }

        private SIPRegistrarBinding GetNextExpiredBinding(DateTimeOffset expiryTime)
        {
            using (var trans = new TransactionScope())
            {
                SIPRegistrarBinding binding = m_bindingsPersistor.Get(b => b.ExpiryTime < expiryTime);

                if (binding != null)
                {
                    if (binding.ExpiryTime < DateTimeOffset.UtcNow.AddSeconds(BINDING_EXPIRY_GRACE_PERIOD * -1))
                    {
                        m_bindingsPersistor.Delete(binding);
                    }
                    else
                    {
                        logger.Warn("A binding returned from the database as expired wasn't. " + binding.SIPAccountName + " and " + binding.MangledContactURI + ", last register " +
                                binding.LastUpdate.ToString("HH:mm:ss") + ", expiry " + binding.Expiry + ", expiry time " + binding.ExpiryTime.ToString("HH:mm:ss") + 
                                ", checkedtime " + expiryTime.ToString("HH:mm:ss") + ", now " + DateTimeOffset.UtcNow.ToString("HH:mm:ss") + ".");

                        binding = null;
                    }
                }

                trans.Complete();

                return binding;
            }
        }

        /// <summary>
        /// Updates the bindings list for a registrar's address-of-records.
        /// </summary>
        /// <param name="proxyEndPoint">If the request arrived at this registrar via a proxy then this will contain the end point of the proxy.</param>
        /// <param name="uacRecvdEndPoint">The public end point the UAC REGISTER request was deemded to have originated from.</param>
        /// <param name="registrarEndPoint">The registrar end point the registration request was received on.</param>
        /// <param name="maxAllowedExpiry">The maximum allowed expiry that can be granted to this binding request.</param>
        /// <returns>If the binding update was successful the expiry time for it is returned otherwise 0.</returns>
        public List<SIPRegistrarBinding> UpdateBindings(
            SIPAccount sipAccount,
            SIPEndPoint proxySIPEndPoint,
            SIPEndPoint remoteSIPEndPoint,
            SIPEndPoint registrarSIPEndPoint,
            List<SIPContactHeader> contactHeaders,
            string callId,
            int cseq,
            //int contactHeaderExpiresValue,
            int expiresHeaderValue,
            string userAgent,
            out SIPResponseStatusCodesEnum responseStatus,
            out string responseMessage)
        {
            //logger.Debug("UpdateBinding " + bindingURI.ToString() + ".");

            int maxAllowedExpiry = (m_userAgentConfigs != null) ? m_userAgentConfigs.GetMaxAllowedExpiry(userAgent) : SIPUserAgentConfiguration.DEFAULT_MAX_EXPIRY_SECONDS;
            responseMessage = null;
            string sipAccountAOR = sipAccount.SIPUsername + "@" + sipAccount.SIPDomain;
            responseStatus = SIPResponseStatusCodesEnum.Ok;

            try
            {
                userAgent = (userAgent != null && userAgent.Length > MAX_USERAGENT_LENGTH) ? userAgent.Substring(0, MAX_USERAGENT_LENGTH) : userAgent;

                List<SIPRegistrarBinding> bindings = m_bindingsPersistor.Get(b => b.SIPAccountId == sipAccount.Id, null, 0, Int32.MaxValue);

                foreach (SIPContactHeader contactHeader in contactHeaders)
                {
                    SIPURI bindingURI = contactHeader.ContactURI.CopyOf();
                    int contactHeaderExpiresValue = contactHeader.Expires;
                    int bindingExpiry = 0;

                    if (bindingURI.Host == m_sipRegisterRemoveAll)
                    {
                        if (contactHeaders.Count > 1)
                        {
                            // If a register request specifies remove all it cannot contain any other binding requests.
                            FireSIPMonitorLogEvent(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.Registrar, SIPMonitorEventTypesEnum.BindingRemoval, "Remove all bindings requested for " + sipAccountAOR + " but mutliple bindings specified, rejecting as a bad request.", sipAccount.SIPUsername));
                            responseStatus = SIPResponseStatusCodesEnum.BadRequest;
                            break;
                        }

                        #region Process remove all bindings.

                        if (expiresHeaderValue == 0)
                        {
                            // Removing all bindings for user.
                            FireSIPMonitorLogEvent(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.Registrar, SIPMonitorEventTypesEnum.BindingRemoval, "Remove all bindings requested for " + sipAccountAOR + ".", sipAccount.SIPUsername));

                            // Mark all the current bindings as expired.
                            if (bindings != null && bindings.Count > 0)
                            {
                                for (int index = 0; index < bindings.Count; index++)
                                {
                                    bindings[index].RemovalReason = SIPBindingRemovalReason.ClientExpiredAll;
                                    bindings[index].Expiry = 0;
                                    m_bindingsPersistor.Update(bindings[index]);

                                    // Remove the NAT keep-alive job if present.
                                    if (m_natKeepAliveJobs.ContainsKey(bindings[index].RemoteSIPSocket))
                                    {
                                        m_natKeepAliveJobs[bindings[index].RemoteSIPSocket].CancelJob();
                                    }
                                }
                            }

                            FireSIPMonitorLogEvent(new SIPMonitorMachineEvent(SIPMonitorMachineEventTypesEnum.SIPRegistrarBindingRemoval, sipAccount.Owner, sipAccount.Id.ToString(), SIPURI.ParseSIPURIRelaxed(sipAccountAOR)));

                            responseStatus = SIPResponseStatusCodesEnum.Ok;
                        }
                        else
                        {
                            // Remove all header cannot be present with other headers and must have an Expiry equal to 0.
                            responseStatus = SIPResponseStatusCodesEnum.BadRequest;
                        }

                        #endregion
                    }
                    else
                    {
                        int requestedExpiry = (contactHeaderExpiresValue != -1) ? contactHeaderExpiresValue : expiresHeaderValue;
                        requestedExpiry = (requestedExpiry == -1) ? maxAllowedExpiry : requestedExpiry;   // This will happen if the Expires header and the Expiry on the Contact are both missing.
                        bindingExpiry = (requestedExpiry > maxAllowedExpiry) ? maxAllowedExpiry : requestedExpiry;
                        bindingExpiry = (bindingExpiry < MINIMUM_EXPIRY_SECONDS) ? MINIMUM_EXPIRY_SECONDS : bindingExpiry;

                        bindingURI.Parameters.Remove(m_sipExpiresParameterKey);

                        SIPRegistrarBinding binding = GetBindingForContactURI(bindings, bindingURI.ToString());

                        if (binding != null)
                        {
                            if (requestedExpiry <= 0)
                            {
                                FireSIPMonitorLogEvent(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.Registrar, SIPMonitorEventTypesEnum.BindingExpired, "Binding expired by client for " + sipAccountAOR + " from " + remoteSIPEndPoint.ToString() + ".", sipAccount.SIPUsername));
                                bindings.Remove(binding);
                                m_bindingsPersistor.Delete(binding);
                                bindingExpiry = 0;

                                FireSIPMonitorLogEvent(new SIPMonitorMachineEvent(SIPMonitorMachineEventTypesEnum.SIPRegistrarBindingRemoval, sipAccount.Owner, sipAccount.Id.ToString(), SIPURI.ParseSIPURIRelaxed(sipAccountAOR)));

                                // Remove the NAT keep-alive job if present.
                                if (m_natKeepAliveJobs.ContainsKey(binding.RemoteSIPSocket))
                                {
                                    m_natKeepAliveJobs[binding.RemoteSIPSocket].CancelJob();
                                }
                            }
                            else
                            {
                                FireSIPMonitorLogEvent(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.Registrar, SIPMonitorEventTypesEnum.Registrar, "Binding update request for " + sipAccountAOR + " from " + remoteSIPEndPoint.ToString() + ", expiry requested " + requestedExpiry + "s granted " + bindingExpiry + "s.", sipAccount.Owner));
                                binding.RefreshBinding(bindingExpiry, remoteSIPEndPoint, proxySIPEndPoint, registrarSIPEndPoint, sipAccount.DontMangleEnabled);

                                DateTime startTime = DateTime.Now;
                                m_bindingsPersistor.Update(binding);
                                TimeSpan duration = DateTime.Now.Subtract(startTime);
                                //FireSIPMonitorLogEvent(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.Registrar, SIPMonitorEventTypesEnum.RegistrarTiming, "Binding database update time for " + sipAccountAOR + " took " + duration.TotalMilliseconds + "ms.", null));
                                FireSIPMonitorLogEvent(new SIPMonitorMachineEvent(SIPMonitorMachineEventTypesEnum.SIPRegistrarBindingUpdate, sipAccount.Owner, sipAccount.Id.ToString(), SIPURI.ParseSIPURIRelaxed(sipAccountAOR)));

                                // Add a NAT keep-alive job if required.
                                if (sipAccount.SendNATKeepAlives && proxySIPEndPoint != null)
                                {
                                    AddNATKeepAliveJob(sipAccount, remoteSIPEndPoint, proxySIPEndPoint, binding, bindingExpiry);
                                }
                            }
                        }
                        else
                        {
                            if (requestedExpiry > 0)
                            {
                                FireSIPMonitorLogEvent(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.Registrar, SIPMonitorEventTypesEnum.BindingInProgress, "New binding request for " + sipAccountAOR + " from " + remoteSIPEndPoint.ToString() + ", expiry requested " + requestedExpiry + "s granted " + bindingExpiry + "s.", sipAccount.Owner));

                                if (bindings.Count >= m_maxBindingsPerAccount)
                                {
                                    // Need to remove the oldest binding to stay within limit.
                                    //SIPRegistrarBinding oldestBinding = m_bindingsPersistor.Get(b => b.SIPAccountId == sipAccount.Id, null, 0, Int32.MaxValue).OrderBy(x => x.LastUpdateUTC).Last();
                                    SIPRegistrarBinding oldestBinding = bindings.OrderBy(x => x.LastUpdate).Last();
                                    FireSIPMonitorLogEvent(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.Registrar, SIPMonitorEventTypesEnum.BindingInProgress, "Binding limit exceeded for " + sipAccountAOR + " from " + remoteSIPEndPoint.ToString() + " removing oldest binding to stay within limit of " + m_maxBindingsPerAccount + ".", sipAccount.Owner));
                                    m_bindingsPersistor.Delete(oldestBinding);

                                    if (m_natKeepAliveJobs.ContainsKey(binding.RemoteSIPSocket))
                                    {
                                        m_natKeepAliveJobs[binding.RemoteSIPSocket].CancelJob();
                                    }
                                }

                                SIPRegistrarBinding newBinding = new SIPRegistrarBinding(sipAccount, bindingURI, callId, cseq, userAgent, remoteSIPEndPoint, proxySIPEndPoint, registrarSIPEndPoint, bindingExpiry);
                                DateTime startTime = DateTime.Now;
                                bindings.Add(newBinding);
                                m_bindingsPersistor.Add(newBinding);
                                TimeSpan duration = DateTime.Now.Subtract(startTime);
                                //FireSIPMonitorLogEvent(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.Registrar, SIPMonitorEventTypesEnum.RegistrarTiming, "Binding database add time for " + sipAccountAOR + " took " + duration.TotalMilliseconds + "ms.", null));

                                // Add a NAT keep-alive job if required.
                                if (sipAccount.SendNATKeepAlives && proxySIPEndPoint != null)
                                {
                                    AddNATKeepAliveJob(sipAccount, remoteSIPEndPoint, proxySIPEndPoint, newBinding, bindingExpiry);
                                }

                                FireSIPMonitorLogEvent(new SIPMonitorMachineEvent(SIPMonitorMachineEventTypesEnum.SIPRegistrarBindingUpdate, sipAccount.Owner, sipAccount.Id.ToString(), SIPURI.ParseSIPURIRelaxed(sipAccountAOR)));
                            }
                            else
                            {
                                FireSIPMonitorLogEvent(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.Registrar, SIPMonitorEventTypesEnum.BindingFailed, "New binding received for " + sipAccountAOR + " with expired contact," + bindingURI.ToString() + " no update.", sipAccount.Owner));
                                bindingExpiry = 0;
                            }
                        }

                        responseStatus = SIPResponseStatusCodesEnum.Ok;
                    }
                }

                return bindings;
            }
            catch (Exception excp)
            {
                logger.Error("Exception UpdateBinding. " + excp.Message);
                FireSIPMonitorLogEvent(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.Registrar, SIPMonitorEventTypesEnum.Error, "Registrar error updating binding: " + excp.Message + " Binding not updated.", sipAccount.SIPUsername));
                responseStatus = SIPResponseStatusCodesEnum.InternalServerError;
                return null;
            }
        }

        private void AddNATKeepAliveJob(SIPAccount sipAccount, SIPEndPoint remoteSIPEndPoint, SIPEndPoint proxySIPEndPoint, SIPRegistrarBinding binding, int bindingExpiry)
        {
            try
            {
                lock (m_natKeepAliveJobs)
                {
                    if (m_natKeepAliveJobs.ContainsKey(binding.RemoteSIPSocket))
                    {
                        //logger.Debug("Updating NAT keep-alive job for binding socket " + binding.RemoteSIPSocket + ".");
                        m_natKeepAliveJobs[binding.RemoteSIPSocket].Update(proxySIPEndPoint, DateTime.Now.AddSeconds(bindingExpiry));
                    }
                    else
                    {
                        //logger.Debug("Adding NAT keep-alive job for binding socket " + binding.RemoteSIPSocket + ".");
                        m_natKeepAliveJobs.Add(binding.RemoteSIPSocket, new NATKeepAliveJob(proxySIPEndPoint, remoteSIPEndPoint, DateTime.Now.AddSeconds(bindingExpiry), sipAccount.Owner));
                    }
                }
            }
            catch (Exception natAddExcp)
            {
                logger.Error("Exception AddNATKeepAliveJob for SIP account " + sipAccount.SIPUsername + ". " + natAddExcp.Message);
            }
        }

        public List<SIPRegistrarBinding> GetBindings(Guid sipAccountId)
        {
            return m_bindingsPersistor.Get(b => b.SIPAccountId == sipAccountId, null, 0, Int32.MaxValue);
        }

        private SIPRegistrarBinding GetBindingForContactURI(List<SIPRegistrarBinding> bindings, string bindingURI)
        {
            if (bindings == null || bindings.Count == 0)
            {
                return null;
            }
            else
            {
                foreach (SIPRegistrarBinding binding in bindings)
                {
                    if (binding.ContactURI == bindingURI)
                    {
                        //logger.Debug(binding.ContactURI + " matched " + bindingURI + ".");
                        return binding;
                    }
                }
                //logger.Debug("No existing binding matched for " + bindingURI + ".");
                return null;
            }
        }

        private void SendNATKeepAlives()
        {
            try
            {
                Thread.CurrentThread.Name = SEND_KEEPALIVES_THREAD_NAME;

                while (!m_stop)
                {
                    try
                    {
                        //List<NATKeepAliveJob> m_jobsList = m_natKeepAliveJobs.Values.ToList();
                        List<string> jobsToRemove = new List<string>();

                        DateTime natKeepAliveStart = DateTime.Now;
                        int natKeepAliveCount = 0;

                        // Send NAT keep-alives.
                        //for (int index = 0; index < m_jobsList.Count; index++) {
                        m_natKeepAliveJobs.Values.ToList().ForEach((job) =>
                        {
                            //NATKeepAliveJob job = m_jobsList[index];
                            try
                            {
                                if (job.EndTime < DateTime.Now || job.Cancel)
                                {
                                    if (!jobsToRemove.Contains(job.RemoteEndPoint.ToString()))
                                    {
                                        logger.Debug("Removing NAT keep-alive job for binding socket " + job.RemoteEndPoint.ToString() + ".");
                                        jobsToRemove.Add(job.RemoteEndPoint.ToString());
                                    }
                                }
                                else if (job.NextSendTime == null || job.NextSendTime < DateTime.Now)
                                {
                                    SendNATKeepAlive_External(new NATKeepAliveMessage(job.ProxyEndPoint, job.RemoteEndPoint.GetIPEndPoint()));
                                    job.NextSendTime = DateTime.Now.AddSeconds(NATKEEPALIVE_DEFAULTSEND_INTERVAL);
                                    //logger.Debug("Requesting NAT keep-alive from proxy socket " + job.ProxyEndPoint.ToString() + " to " + job.RemoteEndPoint + ", owner=" + job.Owner + ".");
                                    FireSIPMonitorLogEvent(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.NATKeepAlive, SIPMonitorEventTypesEnum.NATKeepAlive, "Requesting NAT keep-alive from proxy socket " + job.ProxyEndPoint + " to " + job.RemoteEndPoint + ".", job.Owner));
                                    //if (m_natKeepAliveJobs.ContainsKey(job.RemoteEndPoint.ToString()))
                                    //{
                                    //    m_natKeepAliveJobs[job.BindingId] = job;
                                    //}
                                    natKeepAliveCount++;
                                }
                            }
                            catch (Exception natJobExcp)
                            {
                                logger.Error("Exception attempting NAT keep-alive send for " + job.RemoteEndPoint + ", owner=" + job.Owner + ". " + natJobExcp.Message);
                                if (!jobsToRemove.Contains(job.RemoteEndPoint.ToString()))
                                {
                                    jobsToRemove.Add(job.RemoteEndPoint.ToString());
                                }
                            }
                        });

                        // Remove any flagged jobs.
                        foreach (string removeJob in jobsToRemove)
                        {
                            m_natKeepAliveJobs.Remove(removeJob);
                        }

                        //logger.Debug(natKeepAliveCount + " NAT keep-alives sent, time taken " + DateTime.Now.Subtract(natKeepAliveStart).TotalMilliseconds.ToString("0") + "ms.");
                        //FireSIPMonitorLogEvent(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.Registrar, SIPMonitorEventTypesEnum.Timing, "NATKeepAlive run took " + DateTime.Now.Subtract(natKeepAliveStart).TotalMilliseconds.ToString("0") + "ms.", null));
                    }
                    catch (Exception sendExcp)
                    {
                        logger.Error("Exception SendNATKeepAlives Send. " + sendExcp.Message);
                    }

                    Thread.Sleep(SEND_NATKEEPALIVES_INTERVAL);
                }
            }
            catch (Exception excp)
            {
                logger.Error("Exception SendNATKeepAlives. " + excp.Message);
            }
        }

        private void FireSIPMonitorLogEvent(SIPMonitorEvent monitorEvent)
        {
            if (SIPMonitorEventLog_External != null)
            {
                SIPMonitorEventLog_External(monitorEvent);
            }
        }
    }
}
