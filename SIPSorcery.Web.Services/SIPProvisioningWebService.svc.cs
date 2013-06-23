//-----------------------------------------------------------------------------
// Filename: SIPProvisioningWebService.cs
//
// Description: Web services that expose provisioning services for SIP assets such
// as SIPAccounts, SIPProivders etc. This web service deals with storing objects that need
// to be presisted as oppossed to the manager web service which deals with transient objects
// such as SIP acocunt bindings or registrations.
// 
// History:
// 25 Sep 2008	Aaron Clauson	    Created.
//
// License: 
// This software is licensed under the BSD License http://www.opensource.org/licenses/bsd-license.php
//
// Copyright (c) 2006 Aaron Clauson (aaronc@blueface.ie), Blue Face Ltd, Dublin, Ireland (www.blueface.ie)
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
//-----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq.Dynamic;
using System.Net;
using System.ServiceModel;
using System.ServiceModel.Activation;
using System.ServiceModel.Description;
using System.ServiceModel.Channels;
using System.Text;
using System.Data;
using System.IO;
using System.Text.RegularExpressions;
using System.Timers;
using System.Threading;
using System.Web;
using System.Xml;
using SIPSorcery.CRM;
using SIPSorcery.Persistence;
using SIPSorcery.SIP.App;
using SIPSorcery.Sys;
using log4net;

namespace SIPSorcery.Web.Services
{
    [AspNetCompatibilityRequirements(RequirementsMode = AspNetCompatibilityRequirementsMode.Allowed)]
    public class SIPProvisioningWebService : IProvisioningService, IProvisioningServiceREST {

        public const string AUTH_TOKEN_KEY = "authid";
        private const string NEW_ACCOUNT_EMAIL_FROM_ADDRESS = "admin@sipsorcery.com";
        private const string NEW_ACCOUNT_EMAIL_SUBJECT = "SIPSorcery Account Confirmation";

        private const string NEW_ACCOUNT_EMAIL_BODY =
            "Hi {0},\r\n\r\n" +
            "This is your automated SIPSorcery new account confirmation email.\r\n\r\n" +
            "To confirm your account please visit the link below. If you did not request this email please ignore it.\r\n\r\n" +
            "http://www.sipsorcery.com/customerconfirm.aspx?id={1}\r\n\r\n" +
            "Regards,\r\n\r\n" +
            "SIPSorcery";

        private ILog logger = AppState.GetLogger("provisioning");

        private SIPAssetPersistor<SIPAccount> SIPAccountPersistor;
        private SIPAssetPersistor<SIPDialPlan> DialPlanPersistor;
        private SIPAssetPersistor<SIPProvider> SIPProviderPersistor;
        private SIPAssetPersistor<SIPProviderBinding> SIPProviderBindingsPersistor;
        private SIPAssetPersistor<SIPRegistrarBinding> SIPRegistrarBindingsPersistor;
        private SIPAssetPersistor<SIPDialogueAsset> SIPDialoguePersistor;
        private SIPAssetPersistor<SIPCDRAsset> SIPCDRPersistor;
        private SIPAssetPersistor<Customer> CRMCustomerPersistor;
        private CustomerSessionManager CRMSessionManager;
        private SIPDomainManager SIPDomainManager;
        private SIPMonitorLogDelegate LogDelegate_External = (e) => { };

        private int m_newCustomersAllowedLimit;

        public SIPProvisioningWebService() { }

        public SIPProvisioningWebService(
            SIPAssetPersistor<SIPAccount> sipAccountPersistor,
            SIPAssetPersistor<SIPDialPlan> sipDialPlanPersistor,
            SIPAssetPersistor<SIPProvider> sipProviderPersistor,
            SIPAssetPersistor<SIPProviderBinding> sipProviderBindingsPersistor,
            SIPAssetPersistor<SIPRegistrarBinding> sipRegistrarBindingsPersistor,
            SIPAssetPersistor<SIPDialogueAsset> sipDialoguePersistor,
            SIPAssetPersistor<SIPCDRAsset> sipCDRPersistor,
            CustomerSessionManager crmSessionManager,
            SIPDomainManager sipDomainManager,
            SIPMonitorLogDelegate log,
            int newCustomersAllowedLimit) {

            SIPAccountPersistor = sipAccountPersistor;
            DialPlanPersistor = sipDialPlanPersistor;
            SIPProviderPersistor = sipProviderPersistor;
            SIPProviderBindingsPersistor = sipProviderBindingsPersistor;
            SIPRegistrarBindingsPersistor = sipRegistrarBindingsPersistor;
            SIPDialoguePersistor = sipDialoguePersistor;
            SIPCDRPersistor = sipCDRPersistor;
            CRMCustomerPersistor = crmSessionManager.CustomerPersistor;
            CRMSessionManager = crmSessionManager;
            SIPDomainManager = sipDomainManager;
            LogDelegate_External = log;
            m_newCustomersAllowedLimit = newCustomersAllowedLimit;
        }

        private string GetAuthId() {
            string authId = null;

            if (OperationContext.Current.IncomingMessageHeaders.FindHeader(AUTH_TOKEN_KEY, "") != -1) {
                authId = OperationContext.Current.IncomingMessageHeaders.GetHeader<string>(AUTH_TOKEN_KEY, "");
            }
            
            if (authId.IsNullOrBlank() && HttpContext.Current != null) {
                // If running in IIS check for a cookie.
                HttpCookie authIdCookie = HttpContext.Current.Request.Cookies[AUTH_TOKEN_KEY];
                if (authIdCookie != null) {
                    logger.Debug("authid cookie found: " + authIdCookie.Value + ".");
                    authId = authIdCookie.Value;
                }
            }
            return authId;
        }

        private Customer AuthoriseRequest() {
            try {
                string authId = GetAuthId();
                //logger.Debug("Authorising request for sessionid=" + authId + ".");

                if (authId != null) {
                    CustomerSession customerSession = CRMSessionManager.Authenticate(authId);
                    if (customerSession == null) {
                        logger.Warn("SIPProvisioningWebService AuthoriseRequest failed for " + authId + ".");
                        throw new UnauthorizedAccessException();
                    }
                    else {
                        Customer customer = CRMCustomerPersistor.Get(c => c.CustomerUsername == customerSession.CustomerUsername);
                        return customer;
                    }
                }
                else {
                    logger.Warn("SIPProvisioningWebService AuthoriseRequest failed no authid header.");
                    throw new UnauthorizedAccessException();
                }
            }
            catch (UnauthorizedAccessException) {
                throw;
            }
            catch (Exception excp) {
                logger.Error("Exception AuthoriseRequest. " + excp.Message);
                throw new Exception("There was an exception authorising the request.");
            }
        }

        private string GetAuthorisedWhereExpression(Customer customer, string whereExpression) {
            try {
                if (customer == null) {
                    throw new ArgumentNullException("customer", "The customer cannot be empty when building authorised where expression.");
                }

                string authorisedWhereExpression = "owner=\"" + customer.CustomerUsername + "\"";
                if (customer.AdminId == Customer.TOPLEVEL_ADMIN_ID) {
                    // This user is the top level administrator and has permission to view all system assets.
                    //authorisedWhereExpression = "true";
                    authorisedWhereExpression = null;
                }
                else if (!customer.AdminId.IsNullOrBlank()) {
                    authorisedWhereExpression =
                        "(owner=\"" + customer.CustomerUsername + "\" or adminmemberid=\"" + customer.AdminId + "\")";
                }

                if (!whereExpression.IsNullOrBlank()) {
                    authorisedWhereExpression += " and " + whereExpression;
                }

                return authorisedWhereExpression;
            }
            catch (Exception excp) {
                logger.Error("Exception GetAuthorisedWhereExpression. " + excp.Message);
                throw new Exception("There was an exception constructing the authorisation filter for the request.");
            }
        }

        public bool IsAlive() {
            logger.Debug("IsAlive called from " + OperationContext.Current.Channel.RemoteAddress + ".");
            return true;
        }

        public bool AreNewAccountsEnabled() {
            logger.Debug("AreNewAccountsEnabled called from " + OperationContext.Current.Channel.RemoteAddress + ".");
            return m_newCustomersAllowedLimit == 0 || CRMCustomerPersistor.Count(null) < m_newCustomersAllowedLimit;
        }

        public void CreateCustomer(Customer customer) {
            try {
                // Check whether the number of customers is within the allowed limit.
                if (m_newCustomersAllowedLimit != 0 && CRMCustomerPersistor.Count(null) >= m_newCustomersAllowedLimit) {
                    throw new ApplicationException("Sorry new account creations are currently disabled. Please monitor sipsorcery.wordpress.com for updates.");
                }
                else {
                    // Check whether the username is already taken.
                    customer.CustomerUsername = customer.CustomerUsername.ToLower();
                    Customer existingCustomer = CRMCustomerPersistor.Get(c => c.CustomerUsername == customer.CustomerUsername);
                    if (existingCustomer != null) {
                        throw new ApplicationException("The requested username is already in use please try a different one.");
                    }

                    // Check whether the email address is already taken.
                    customer.EmailAddress = customer.EmailAddress.ToLower();
                    existingCustomer = CRMCustomerPersistor.Get(c => c.EmailAddress == customer.EmailAddress);
                    if (existingCustomer != null) {
                        throw new ApplicationException("The email address is already associated with an account.");
                    }

                    string validationError = Customer.ValidateAndClean(customer);
                    if (validationError != null) {
                        throw new ApplicationException(validationError);
                    }

                    customer.MaxExecutionCount = Customer.DEFAULT_MAXIMUM_EXECUTION_COUNT;

                    CRMCustomerPersistor.Add(customer);
                    logger.Debug("New customer record added for " + customer.CustomerUsername + ".");

                    // Create a default dialplan.
                    SIPDialPlan defaultDialPlan = new SIPDialPlan(customer.CustomerUsername, "default", null, "sys.Log(\"hello world\")\n", SIPDialPlanScriptTypesEnum.Ruby);
                    DialPlanPersistor.Add(defaultDialPlan);
                    logger.Debug("Default dialplan added for " + customer.CustomerUsername + ".");

                    // Get default domain name.
                    string defaultDomain = SIPDomainManager.GetDomain("local", true);

                    // Create SIP account.
                    if (SIPAccountPersistor.Get(s => s.SIPUsername == customer.CustomerUsername && s.SIPDomain == defaultDomain) == null) {
                        SIPAccount sipAccount = new SIPAccount(customer.CustomerUsername, defaultDomain, customer.CustomerUsername, customer.CustomerPassword, "default");
                        SIPAccountPersistor.Add(sipAccount);
                        logger.Debug("SIP account " + sipAccount.SIPUsername + "@" + sipAccount.SIPDomain + " added for " + sipAccount.Owner + ".");
                    }
                    else {
                        int attempts = 0;
                        while (attempts < 10) {
                            string testUsername = customer.CustomerUsername + Crypto.GetRandomString(4);
                            if (SIPAccountPersistor.Get(s => s.SIPUsername == testUsername && s.SIPDomain == defaultDomain) == null) {
                                SIPAccount sipAccount = new SIPAccount(customer.CustomerUsername, defaultDomain, testUsername, customer.CustomerPassword, "default");
                                SIPAccountPersistor.Add(sipAccount);
                                logger.Debug("SIP account " + sipAccount.SIPUsername + "@" + sipAccount.SIPDomain + " added for " + sipAccount.Owner + ".");
                                break;
                            }
                            else {
                                attempts++;
                            }
                        }
                    }

                    logger.Debug("Sending new account confirmation email to " + customer.EmailAddress + ".");
                    Email.SendEmail(customer.EmailAddress, NEW_ACCOUNT_EMAIL_FROM_ADDRESS, NEW_ACCOUNT_EMAIL_SUBJECT, String.Format(NEW_ACCOUNT_EMAIL_BODY, customer.FirstName, customer.Id));
                }
            }
            catch (Exception excp) {
                logger.Error("Exception CreateNewCustomer. " + excp.Message);
                throw;
            }
        }

        public void DeleteCustomer(string customerUsername) {
            try {
                Customer customer = AuthoriseRequest();
                if (customer != null && customer.CustomerUsername == customerUsername) {
                    CRMCustomerPersistor.Delete(customer);
                    logger.Debug("Customer account " + customer.CustomerUsername + " successfully deleted.");
                }
                else {
                    logger.Warn("Unauthorised attempt to delete customer " + customerUsername + ".");
                }
            }
            catch (Exception excp) {
                logger.Error("Exception DeleteCustomer. " + excp.Message);
            }
        }

        public string Login(string username, string password) {
            logger.Debug("SIPProvisioningWebService Login called for " + username + ".");

            if (username == null || username.Trim().Length == 0) {
                return null;
            }
            else {
                string ipAddress = null;
                OperationContext context = OperationContext.Current;
                MessageProperties properties = context.IncomingMessageProperties;
                RemoteEndpointMessageProperty endpoint = properties[RemoteEndpointMessageProperty.Name] as RemoteEndpointMessageProperty;
                if (endpoint != null) {
                    ipAddress = endpoint.Address;
                }

                CustomerSession customerSession = CRMSessionManager.Authenticate(username, password, ipAddress);
                if (customerSession != null) {
                    
                    // If running in IIS add a cookie for javascript clients.
                    if (HttpContext.Current != null) {
                        logger.Debug("Setting authid cookie for " + customerSession.CustomerUsername + ".");
                        HttpCookie authCookie = new HttpCookie(AUTH_TOKEN_KEY, customerSession.SessionID);
                        authCookie.Secure = HttpContext.Current.Request.IsSecureConnection;
                        authCookie.HttpOnly = true;
                        authCookie.Expires = DateTime.Now.AddMinutes(customerSession.TimeLimitMinutes);
                        HttpContext.Current.Response.Cookies.Set(authCookie);
                    }

                    return customerSession.SessionID;
                }
                else {
                    return null;
                }
            }
        }

        public void ExtendSession(int minutes) {
            try {
                Customer customer = AuthoriseRequest();

                logger.Debug("SIPProvisioningWebService  ExtendSession called for " + customer.CustomerUsername + " and " + minutes + " minutes.");
                if (HttpContext.Current != null) {
                    HttpCookie authIdCookie = HttpContext.Current.Request.Cookies[AUTH_TOKEN_KEY];
                    authIdCookie.Expires = authIdCookie.Expires.AddMinutes(minutes);
                }
                CRMSessionManager.ExtendSession(GetAuthId(), minutes);
            }
            catch (Exception excp) {
                logger.Error("Exception ExtendSession. " + excp.Message);
                throw;
            }
        }

        public void Logout() {
            try {
                Customer customer = AuthoriseRequest();

                logger.Debug("SIPProvisioningWebService Logout called for " + customer.CustomerUsername + ".");
                CRMSessionManager.ExpireToken(GetAuthId());

                // If running in IIS remove the cookie.
                if (HttpContext.Current != null) {
                    HttpContext.Current.Request.Cookies.Remove(AUTH_TOKEN_KEY);
                }

                // Fire a machine log event to disconnect the silverlight tcp socket.
                LogDelegate_External(new SIPMonitorMachineEvent(SIPMonitorMachineEventTypesEnum.Logout, customer.CustomerUsername, null, null));
            }
            catch (UnauthorizedAccessException) {
                // This exception will occur if the SIP Server agent is restarted and the client sends a previously valid token.
                //logger.Debug("An unauthorised exception was thrown in logout.");
            }
            catch (Exception excp) {
                logger.Error("Exception Logout. " + excp.Message);
            }
        }

        public Customer GetCustomer(string username) {
            Customer customer = AuthoriseRequest();

            if (customer.CustomerUsername == username) {
                return customer;
            }
            else {
                throw new ApplicationException("You are not authorised to retrieve customer for username " + username + ".");
            }
        }

        public int GetTimeZoneOffsetMinutes() {
            try {
                Customer customer = AuthoriseRequest();

                if (!customer.TimeZone.IsNullOrBlank()) {
                    foreach (TimeZoneInfo timezone in TimeZoneInfo.GetSystemTimeZones()) {
                        if (timezone.DisplayName == customer.TimeZone) {
                            return (int)timezone.BaseUtcOffset.TotalMinutes;
                        }
                    }
                }

                return 0;
            }
            catch (Exception excp) {
                logger.Error("Exception GetTimeZoneOffsetMinutes. " + excp.Message);
                return 0;
            }
        }

        public void UpdateCustomer(Customer updatedCustomer) {
            Customer customer = AuthoriseRequest();

            if (customer.CustomerUsername == updatedCustomer.CustomerUsername) {
                logger.Debug("Updating customer details for " + customer.CustomerUsername);
                customer.FirstName = updatedCustomer.FirstName;
                customer.LastName = updatedCustomer.LastName;
                customer.EmailAddress = updatedCustomer.EmailAddress;
                customer.SecurityQuestion = updatedCustomer.SecurityQuestion;
                customer.SecurityAnswer = updatedCustomer.SecurityAnswer;
                customer.City = updatedCustomer.City;
                customer.Country = updatedCustomer.Country;
                customer.WebSite = updatedCustomer.WebSite;
                customer.TimeZone = updatedCustomer.TimeZone;

                string validationError = Customer.ValidateAndClean(customer);
                if (validationError != null) {
                    throw new ApplicationException(validationError);
                }

                CRMCustomerPersistor.Update(customer);
            }
            else {
                throw new ApplicationException("You are not authorised to update customer for username " + updatedCustomer.CustomerUsername + ".");
            }
        }

        public void UpdateCustomerPassword(string username, string oldPassword, string newPassword) {
            Customer customer = AuthoriseRequest();

            if (customer.CustomerUsername == username) {
                if (customer.CustomerPassword != oldPassword) {
                    throw new ApplicationException("The existing password did not match when attempting a password update.");
                }
                else {
                    logger.Debug("Updating customer password for " + customer.CustomerUsername);
                    customer.CustomerPassword = newPassword;
                    CRMCustomerPersistor.Update(customer);
                }
            }
            else {
                throw new ApplicationException("You are not authorised to update customer password for username " + username + ".");
            }
        }

        public List<SIPDomain> GetSIPDomains(string filterExpression, int offset, int count) {
            Customer customer = AuthoriseRequest();

            if (customer == null) {
                throw new ArgumentNullException("customer", "The customer cannot be empty when building authorised where expression.");
            }
            else {
                string authoriseExpression = "owner =\"" + customer.CustomerUsername + "\" or owner = null";
                //logger.Debug("SIPProvisioningWebService GetSIPDomains called for " + customer.CustomerUsername + " and where: " + authoriseExpression + ".");
                return SIPDomainManager.Get(DynamicExpression.ParseLambda<SIPDomain, bool>(authoriseExpression), offset, count);
            }
        }

        public int GetSIPAccountsCount(string whereExpression) {
            Customer customer = AuthoriseRequest();

            string authoriseExpression = GetAuthorisedWhereExpression(customer, whereExpression);
            //logger.Debug("SIPProvisioningWebService GetSIPAccountsCount called for " + customerSession.Customer.CustomerUsername + " and where: " + authoriseExpression + ".");

            if (authoriseExpression.IsNullOrBlank()) {
                return SIPAccountPersistor.Count(null);
            }
            else {
                return SIPAccountPersistor.Count(DynamicExpression.ParseLambda<SIPAccount, bool>(authoriseExpression));
            }
        }

        public List<SIPAccount> GetSIPAccounts(string whereExpression, int offset, int count) {
            Customer customer = AuthoriseRequest();

            string authoriseExpression = GetAuthorisedWhereExpression(customer, whereExpression);
            //logger.Debug("SIPProvisioningWebService GetSIPAccountscalled for " + customerSession.Customer.CustomerUsername + " and where: " + authoriseExpression + ".");

            if (authoriseExpression.IsNullOrBlank()) {
                return SIPAccountPersistor.Get(null, "sipusername", offset, count);
            }
            else {
                return SIPAccountPersistor.Get(DynamicExpression.ParseLambda<SIPAccount, bool>(authoriseExpression), "sipusername", offset, count);
            }
        }

        public SIPAccount AddSIPAccount(SIPAccount sipAccount) {
            Customer customer = AuthoriseRequest();
            sipAccount.Owner = customer.CustomerUsername;

            string validationError = SIPAccount.ValidateAndClean(sipAccount);
            if (validationError != null) {
                logger.Warn("Validation error in AddSIPAccount for customer " + customer.CustomerUsername + ". " + validationError);
                throw new ApplicationException(validationError);
            }
            else {
                return SIPAccountPersistor.Add(sipAccount);
            }
        }

        public SIPAccount UpdateSIPAccount(SIPAccount sipAccount) {
            Customer customer = AuthoriseRequest();

            if (customer.AdminId != Customer.TOPLEVEL_ADMIN_ID && sipAccount.Owner != customer.CustomerUsername) {
                logger.Debug("Unauthorised attempt to update SIP account by user=" + customer.CustomerUsername + ", on account owned by=" + sipAccount.Owner + ".");
                throw new ApplicationException("You are not authorised to update the SIP Account.");
            }

            string validationError = SIPAccount.ValidateAndClean(sipAccount);
            if (validationError != null) {
                logger.Warn("Validation error in UpdateSIPAccount for customer " + customer.CustomerUsername + ". " + validationError);
                throw new ApplicationException(validationError);
            }
            else {
                return SIPAccountPersistor.Update(sipAccount);
            }
        }

        public SIPAccount DeleteSIPAccount(SIPAccount sipAccount) {
            Customer customer = AuthoriseRequest();

            if (customer.AdminId != Customer.TOPLEVEL_ADMIN_ID && sipAccount.Owner != customer.CustomerUsername) {
                throw new ApplicationException("You are not authorised to delete the SIP Account.");
            }

            SIPAccountPersistor.Delete(sipAccount);

            // Enables the caller to see which SIP account has been deleted.
            return sipAccount;
        }

        public int GetSIPRegistrarBindingsCount(string whereExpression) {
            Customer customer = AuthoriseRequest();
            string authoriseExpression = GetAuthorisedWhereExpression(customer, whereExpression);
            //logger.Debug("SIPProvisioningWebService GetSIPRegistrarBindingsCount for " + customerSession.Customer.CustomerUsername + " and where: " + authoriseExpression + ".");

            if (authoriseExpression.IsNullOrBlank()) {
                return SIPRegistrarBindingsPersistor.Count(null);
            }
            else {
                return SIPRegistrarBindingsPersistor.Count(DynamicExpression.ParseLambda<SIPRegistrarBinding, bool>(authoriseExpression));
            }
        }

        public List<SIPRegistrarBinding> GetSIPRegistrarBindings(string whereExpression, int offset, int count) {
            Customer customer = AuthoriseRequest();
            string authoriseExpression = GetAuthorisedWhereExpression(customer, whereExpression);
            //logger.Debug("SIPProvisioningWebService GetSIPRegistrarBindings for " + customerSession.Customer.CustomerUsername + " and where: " + authoriseExpression + ".");

            if (authoriseExpression.IsNullOrBlank()) {
                return SIPRegistrarBindingsPersistor.Get(null, "sipaccountname", offset, count);
            }
            else {
                return SIPRegistrarBindingsPersistor.Get(DynamicExpression.ParseLambda<SIPRegistrarBinding, bool>(authoriseExpression), "sipaccountname", offset, count);
            }
        }

        public int GetSIPProvidersCount(string whereExpression) {
            Customer customer = AuthoriseRequest();
            string authoriseExpression = GetAuthorisedWhereExpression(customer, whereExpression);
            //logger.Debug("SIPProvisioningWebService GetSIPProvidersCount for " + customer.CustomerUsername + " and where: " + authoriseExpression + ".");

            if (authoriseExpression.IsNullOrBlank()) {
                return SIPProviderPersistor.Count(null);
            }
            else {
                return SIPProviderPersistor.Count(DynamicExpression.ParseLambda<SIPProvider, bool>(authoriseExpression));
            }
        }

        public List<SIPProvider> GetSIPProviders(string whereExpression, int offset, int count) {
            Customer customer = AuthoriseRequest();
            string authoriseExpression = GetAuthorisedWhereExpression(customer, whereExpression);

            if (authoriseExpression.IsNullOrBlank()) {
                return SIPProviderPersistor.Get(null, "providername", offset, count);
            }
            else {
                //logger.Debug("SIPProvisioningWebService GetSIPProviders for " + customerSession.Customer.CustomerUsername + " and where: " + authoriseExpression + ".");
                return SIPProviderPersistor.Get(DynamicExpression.ParseLambda<SIPProvider, bool>(authoriseExpression), "providername", offset, count);
            }
        }

        public SIPProvider AddSIPProvider(SIPProvider sipProvider) {
            Customer customer = AuthoriseRequest();
            sipProvider.Owner = customer.CustomerUsername;

            string validationError = SIPProvider.ValidateAndClean(sipProvider);
            if (validationError != null) {
                logger.Warn("Validation error in AddSIPProvider for customer " + customer.CustomerUsername + ". " + validationError);
                throw new ApplicationException(validationError);
            }
            else {
                return SIPProviderPersistor.Add(sipProvider);
            }
        }

        public SIPProvider UpdateSIPProvider(SIPProvider sipProvider) {
            Customer customer = AuthoriseRequest();

            if (customer.AdminId != Customer.TOPLEVEL_ADMIN_ID && sipProvider.Owner != customer.CustomerUsername) {
                throw new ApplicationException("You are not authorised to update the SIP Provider.");
            }

            string validationError = SIPProvider.ValidateAndClean(sipProvider);
            if (validationError != null) {
                logger.Warn("Validation error in UpdateSIPProvider for customer " + customer.CustomerUsername + ". " + validationError);
                throw new ApplicationException(validationError);
            }
            else {
                return SIPProviderPersistor.Update(sipProvider);
            }
        }

        public SIPProvider DeleteSIPProvider(SIPProvider sipProvider) {
            Customer customer = AuthoriseRequest();

            if (customer.AdminId != Customer.TOPLEVEL_ADMIN_ID && sipProvider.Owner != customer.CustomerUsername) {
                throw new ApplicationException("You are not authorised to delete the SIP Provider.");
            }

            //logger.Debug("DeleteSIPProvider, owner=" + sipProvider.Owner + ", providername=" + sipProvider.ProviderName + ".");
            SIPProviderPersistor.Delete(sipProvider);

            // Enables the caller to see which SIP Provider has been deleted.
            return sipProvider;
        }

        public int GetSIPProviderBindingsCount(string whereExpression) {
            Customer customer = AuthoriseRequest();
            string authoriseExpression = GetAuthorisedWhereExpression(customer, whereExpression);

            if (authoriseExpression.IsNullOrBlank()) {
                return SIPProviderBindingsPersistor.Count(null);
            }
            else {
                return SIPProviderBindingsPersistor.Count(DynamicExpression.ParseLambda<SIPProviderBinding, bool>(authoriseExpression));
            }
        }

        public List<SIPProviderBinding> GetSIPProviderBindings(string whereExpression, int offset, int count) {
            Customer customer = AuthoriseRequest();
            string authoriseExpression = GetAuthorisedWhereExpression(customer, whereExpression);

            if (authoriseExpression.IsNullOrBlank()) {
                return SIPProviderBindingsPersistor.Get(null, "providername asc", offset, count);
            }
            else {
                return SIPProviderBindingsPersistor.Get(DynamicExpression.ParseLambda<SIPProviderBinding, bool>(authoriseExpression), "providername asc", offset, count);
            }
        }

        public int GetDialPlansCount(string whereExpression) {
            Customer customer = AuthoriseRequest();
            string authoriseExpression = GetAuthorisedWhereExpression(customer, whereExpression);
            //logger.Debug("SIPProvisioningWebService GetDialPlansCount for " + customerSession.Customer.CustomerUsername + " and where: " + authoriseExpression + ".");

            if (authoriseExpression.IsNullOrBlank()) {
                return DialPlanPersistor.Count(null);
            }
            else {
                return DialPlanPersistor.Count(DynamicExpression.ParseLambda<SIPDialPlan, bool>(authoriseExpression));
            }
        }

        public List<SIPDialPlan> GetDialPlans(string whereExpression, int offset, int count) {
            Customer customer = AuthoriseRequest();
            string authoriseExpression = GetAuthorisedWhereExpression(customer, whereExpression);
            logger.Debug("SIPProvisioningWebService GetDialPlans for " + customer.CustomerUsername + " and where: " + authoriseExpression + ".");

            if (authoriseExpression.IsNullOrBlank()) {
                return DialPlanPersistor.Get(null, "dialplanname asc", offset, count);
            }
            else {
                return DialPlanPersistor.Get(DynamicExpression.ParseLambda<SIPDialPlan, bool>(authoriseExpression), "dialplanname asc", offset, count);
            }
        }

        public SIPDialPlan AddDialPlan(SIPDialPlan dialPlan) {
            Customer customer = AuthoriseRequest();

            dialPlan.Owner = customer.CustomerUsername;

            if (customer.AdminId != Customer.TOPLEVEL_ADMIN_ID) {
                dialPlan.MaxExecutionCount = SIPDialPlan.DEFAULT_MAXIMUM_EXECUTION_COUNT;
            }

            return DialPlanPersistor.Add(dialPlan);
        }

        public SIPDialPlan UpdateDialPlan(SIPDialPlan dialPlan) {
            Customer customer = AuthoriseRequest();

            if (customer.AdminId != Customer.TOPLEVEL_ADMIN_ID && dialPlan.Owner != customer.CustomerUsername) {
                throw new ApplicationException("You are not authorised to update the Dial Plan.");
            }

            return DialPlanPersistor.Update(dialPlan);
        }

        public SIPDialPlan DeleteDialPlan(SIPDialPlan dialPlan) {
            Customer customer = AuthoriseRequest();

            if (customer.AdminId != Customer.TOPLEVEL_ADMIN_ID && dialPlan.Owner != customer.CustomerUsername) {
                throw new ApplicationException("You are not authorised to delete the Dial Plan.");
            }

            DialPlanPersistor.Delete(dialPlan);

            // Enables the caller to see which dialplan has been deleted.
            return dialPlan;
        }

        public int GetCallsCount(string whereExpression) {
            Customer customer = AuthoriseRequest();

            string authoriseExpression = GetAuthorisedWhereExpression(customer, whereExpression);
            //logger.Debug("SIPProvisioningWebService GetCallsCount for " + customerSession.Customer.CustomerUsername + " and where: " + authoriseExpression + ".");

            if (authoriseExpression.IsNullOrBlank()) {
                return SIPDialoguePersistor.Count(null);
            }
            else {
                return SIPDialoguePersistor.Count(DynamicExpression.ParseLambda<SIPDialogueAsset, bool>(authoriseExpression));
            }
        }

        public List<SIPDialogueAsset> GetCalls(string whereExpression, int offset, int count) {
            Customer customer = AuthoriseRequest();

            string authorisedExpression = GetAuthorisedWhereExpression(customer, whereExpression);
            //logger.Debug("SIPProvisioningWebService GetCalls for " + customerSession.Customer.CustomerUsername + " and where: " + authoriseExpression + ".");

            if (authorisedExpression.IsNullOrBlank()) {
                return SIPDialoguePersistor.Get(null, null, offset, count);
            }
            else {
                return SIPDialoguePersistor.Get(DynamicExpression.ParseLambda<SIPDialogueAsset, bool>(authorisedExpression), null, offset, count);
            }
        }

        public int GetCDRsCount(string whereExpression) {
            Customer customer = AuthoriseRequest();

            string authoriseExpression = GetAuthorisedWhereExpression(customer, whereExpression);
            //logger.Debug("SIPProvisioningWebService GetCDRsCount for " + customerSession.Customer.CustomerUsername + " and where: " + authoriseExpression + ".");

            if (authoriseExpression.IsNullOrBlank()) {
                return SIPCDRPersistor.Count(null);
            }
            else {
                return SIPCDRPersistor.Count(DynamicExpression.ParseLambda<SIPCDRAsset, bool>(authoriseExpression));
            }
        }

        public List<SIPCDRAsset> GetCDRs(string whereExpression, int offset, int count) {
            Customer customer = AuthoriseRequest();

            string authoriseExpression = GetAuthorisedWhereExpression(customer, whereExpression);
            //logger.Debug("SIPProvisioningWebService GetCDRs for " + customerSession.Customer.CustomerUsername + " and where: " + authoriseExpression + ".");

            if (authoriseExpression.IsNullOrBlank()) {
                return SIPCDRPersistor.Get(null, "created desc", offset, count);
            }
            else {
                return SIPCDRPersistor.Get(DynamicExpression.ParseLambda<SIPCDRAsset, bool>(authoriseExpression), "created desc", offset, count);
            }
        }
    }
}

