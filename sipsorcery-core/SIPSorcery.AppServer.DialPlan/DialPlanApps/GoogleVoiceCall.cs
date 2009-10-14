﻿//-----------------------------------------------------------------------------
// Filename: GoogleVoiceCall.cs
//
// Description: A dial plan command that places HTTP request to initiate a call 
// through the Google Voice service and bridges the callback with the original caller.
// 
// History:
// 11 Aug 2009	    Aaron Clauson	    Created.
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
//-----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Sys;
using log4net;

namespace SIPSorcery.AppServer.DialPlan {
    
    public class GoogleVoiceCall {

        private const string PRE_LOGIN_URL = "https://www.google.com/accounts/ServiceLogin";
        private const string LOGIN_URL = "https://www.google.com/accounts/ServiceLoginAuth?service=grandcentral";
        private const string VOICE_HOME_URL = "https://www.google.com/voice";
        private const string VOICE_CALL_URL = "https://www.google.com/voice/call/connect";
        private const int WAIT_FOR_CALLBACK_TIMEOUT = 30;
        private const int HTTP_REQUEST_TIMEOUT = 5;

        private static ILog logger = AppState.logger;
        private SIPMonitorLogDelegate Log_External;

        private SIPTransport m_sipTransport;
        private ISIPCallManager m_callManager;
        private string m_username;
        private string m_adminMemberId;
        private SIPEndPoint m_outboundProxy;

        private string m_forwardingNumber;
        private string m_fromURIUserRegexMatch;
        private ManualResetEvent m_waitForCallback = new ManualResetEvent(false);
        private ISIPServerUserAgent m_callbackCall;

        internal event CallProgressDelegate CallProgress;

        public GoogleVoiceCall(
            SIPTransport sipTransport,
            ISIPCallManager callManager,
            SIPMonitorLogDelegate logDelegate, 
            string username,
            string adminMemberId,
            SIPEndPoint outboundProxy) {

            m_sipTransport = sipTransport;
            m_callManager = callManager;
            Log_External = logDelegate;
            m_username = username;
            m_adminMemberId = adminMemberId;
            m_outboundProxy = outboundProxy;
        }

        /// <summary>
        /// Initiates a Google Voice callback by sending 3 HTTP requests and then waiting for the incoming SIP call.
        /// </summary>
        /// <param name="emailAddress">The Google Voice email address to login with.</param>
        /// <param name="password">The Google Voice password to login with.</param>
        /// <param name="forwardingNumber">The number to request Google Voice to do the intial callback on.</param>
        /// <param name="destinationNumber">The number to request Google Voice to dial out on. This is what Google will attempt to
        /// call once the callback on the forwardingNumber is answered.</param>
        /// <param name="fromUserToMatch">The FromURI user to match to recognise the incoming call. If null it will be assumed that
        /// Gizmo is being used and the X-GoogleVoice header will be used.</param>
        /// <param name="contentType">The content type of the SIP call into sipsorcery that created the Google Voice call. It is
        /// what will be sent in the Ok response to the initial incoming callback.</param>
        /// <param name="body">The content of the SIP call into sipsorcery that created the Google Voice call. It is
        /// what will be sent in the Ok response to the initial incoming callback.</param>
        /// <returns>If successful the dialogue of the established call otherwsie null.</returns>
        public SIPDialogue InitiateCall(string emailAddress, string password, string forwardingNumber, string destinationNumber, string fromUserRegexMatch, string contentType, string body) {
            try {
                m_forwardingNumber = forwardingNumber;
                m_fromURIUserRegexMatch = fromUserRegexMatch;

                if (CallProgress != null) {
                    CallProgress(SIPResponseStatusCodesEnum.Ringing, "Initiating Google Voice call", null, null, null);
                }

                CookieContainer cookies = new CookieContainer();
                string rnr = Login(cookies, emailAddress, password);
                if (!rnr.IsNullOrBlank()) {
                    Log_External(new SIPMonitorControlClientEvent(SIPMonitorServerTypesEnum.AppServer, SIPMonitorEventTypesEnum.DialPlan, "Call key " + rnr + " successfully retrieved for " + emailAddress + ", proceeding with callback.", m_username));
                    return SendCallRequest(cookies, forwardingNumber, destinationNumber, rnr, contentType, body);
                }
                else {
                    Log_External(new SIPMonitorControlClientEvent(SIPMonitorServerTypesEnum.AppServer, SIPMonitorEventTypesEnum.DialPlan, "Call key was not etrieved for " + emailAddress + " callback cannot proceed.", m_username));
                    return null;
                }
            }
            catch (Exception excp) {
                logger.Error("Exception GoogleVoiceCall InitiateCall. " + excp.Message);
                throw;
            }
        }

        private string Login(CookieContainer cookies, string emailAddress, string password) {
            try {
                Log_External(new SIPMonitorControlClientEvent(SIPMonitorServerTypesEnum.AppServer, SIPMonitorEventTypesEnum.DialPlan, "Logging into google.com for " + emailAddress + ".", m_username));

                // Fetch GALX
                HttpWebRequest galxRequest = (HttpWebRequest)WebRequest.Create(PRE_LOGIN_URL);
                galxRequest.ConnectionGroupName = "prelogin";
                galxRequest.CookieContainer = cookies;

                HttpWebResponse galxResponse = (HttpWebResponse)galxRequest.GetResponse();
                if (galxResponse.StatusCode != HttpStatusCode.OK) {
                    galxResponse.Close();
                    throw new ApplicationException("Load of the Google Voice pre-login page failed with response " + galxResponse.StatusCode + ".");
                }
                else {
                    Log_External(new SIPMonitorControlClientEvent(SIPMonitorServerTypesEnum.AppServer, SIPMonitorEventTypesEnum.DialPlan, "Google Voice pre-login page loaded successfully.", m_username));
                }

                StreamReader galxReader = new StreamReader(galxResponse.GetResponseStream());
                string galxResponseFromServer = galxReader.ReadToEnd();
                galxResponse.Close();

                Match galxMatch = Regex.Match(galxResponseFromServer, @"name=""GALX""\s+?value=""(?<galxvalue>.*?)""");
                if (galxMatch.Success) {
                    Log_External(new SIPMonitorControlClientEvent(SIPMonitorServerTypesEnum.AppServer, SIPMonitorEventTypesEnum.DialPlan, "GALX key " + galxMatch.Result("${galxvalue}") + " successfully retrieved.", m_username));
                }
                else {
                    throw new ApplicationException("Could not find GALX key on your Google Voice pre-login page, callback cannot proceed.");
                }

                // Build login request.
                string loginData = "Email=" + Uri.EscapeDataString(emailAddress) + "&Passwd=" + Uri.EscapeDataString(password) + "&GALX=" + Uri.EscapeDataString(galxMatch.Result("${galxvalue}")); 
                HttpWebRequest loginRequest = (HttpWebRequest)WebRequest.Create(LOGIN_URL);
                loginRequest.CookieContainer = cookies;
                loginRequest.ConnectionGroupName = "login";
                loginRequest.AllowAutoRedirect = true;
                loginRequest.Method = "POST";
                loginRequest.ContentType = "application/x-www-form-urlencoded;charset=utf-8";
                loginRequest.ContentLength = loginData.Length;
                loginRequest.GetRequestStream().Write(Encoding.UTF8.GetBytes(loginData), 0, loginData.Length);
                loginRequest.Timeout = HTTP_REQUEST_TIMEOUT * 1000;

                // Send login request and read response stream.
                HttpWebResponse response = (HttpWebResponse)loginRequest.GetResponse();
                if (response.StatusCode != HttpStatusCode.OK) {
                    response.Close();
                    throw new ApplicationException("Login to google.com failed for " + emailAddress + " with response " + response.StatusCode + ".");
                }
                response.Close();

                // We're now logged in. Need to load up the Google Voice page to get the rnr hidden input value which is needed for
                // the HTTP call requests.
                HttpWebRequest rnrRequest = (HttpWebRequest)WebRequest.Create(VOICE_HOME_URL);
                rnrRequest.ConnectionGroupName = "call";
                rnrRequest.CookieContainer = cookies;

                // Send the Google Voice account page request and read response stream.
                response = (HttpWebResponse)rnrRequest.GetResponse();
                if (response.StatusCode != HttpStatusCode.OK) {
                    response.Close();
                    throw new ApplicationException("Load of the Google Voice account page failed for " + emailAddress + " with response " + response.StatusCode + ".");
                }
                else {
                    Log_External(new SIPMonitorControlClientEvent(SIPMonitorServerTypesEnum.AppServer, SIPMonitorEventTypesEnum.DialPlan, "Google Voice home page loaded successfully.", m_username));
                }
                
                StreamReader reader = new StreamReader(response.GetResponseStream());
                string responseFromServer = reader.ReadToEnd();
                response.Close();

                // Extract the rnr field from the HTML.
                Match rnrMatch = Regex.Match(responseFromServer, @"name=""_rnr_se"".*?value=""(?<rnrvalue>.*?)""");
                if(rnrMatch.Success) {
                    return rnrMatch.Result("${rnrvalue}");
                }
                else {
                    throw new ApplicationException("Could not find _rnr_se key on your Google Voice account page, callback cannot proceed.");
                }
            }
            catch (Exception excp) {
                logger.Error("Exception GoogleVoiceCall Login. " + excp.Message);
                throw;
            }
        }

        private SIPDialogue SendCallRequest(CookieContainer cookies, string forwardingNumber, string destinationNumber, string rnr, string contentType, string body) {
            try {
                CallbackWaiter callbackWaiter = new CallbackWaiter(CallbackWaiterEnum.GoogleVoice, forwardingNumber, MatchIncomingCall);
                m_callManager.AddWaitingApplication(callbackWaiter);
                
                string callData = "outgoingNumber=" + Uri.EscapeDataString(destinationNumber) + "&forwardingNumber=" + Uri.EscapeDataString(forwardingNumber) + 
                    "&subscriberNumber=undefined&remember=0&_rnr_se=" + Uri.EscapeDataString(rnr);
                logger.Debug("call data=" + callData + ".");

                // Build the call request.
                HttpWebRequest callRequest = (HttpWebRequest)WebRequest.Create(VOICE_CALL_URL);
                callRequest.ConnectionGroupName = "call";
                callRequest.CookieContainer = cookies;
                callRequest.Method = "POST";
                callRequest.ContentType = "application/x-www-form-urlencoded;charset=utf-8";
                callRequest.ContentLength = callData.Length;
                callRequest.GetRequestStream().Write(Encoding.UTF8.GetBytes(callData), 0, callData.Length);
                callRequest.Timeout = HTTP_REQUEST_TIMEOUT * 1000;

                HttpWebResponse response = (HttpWebResponse)callRequest.GetResponse();
                HttpStatusCode responseStatus = response.StatusCode;
                response.Close();
                if (responseStatus != HttpStatusCode.OK) {
                    throw new ApplicationException("The call request failed with a " + responseStatus + " response.");
                }
                else {
                    Log_External(new SIPMonitorControlClientEvent(SIPMonitorServerTypesEnum.AppServer, SIPMonitorEventTypesEnum.DialPlan, "Google Voice Call to " + destinationNumber + " forwarding to " + forwardingNumber + " successfully initiated.", m_username));
                }
                
                if (m_waitForCallback.WaitOne(WAIT_FOR_CALLBACK_TIMEOUT * 1000)) {
                    Log_External(new SIPMonitorControlClientEvent(SIPMonitorServerTypesEnum.AppServer, SIPMonitorEventTypesEnum.DialPlan, "Google Voice Call callback received.", m_username));
                    return m_callbackCall.Answer(contentType, body, null);
                }
                else {
                    Log_External(new SIPMonitorControlClientEvent(SIPMonitorServerTypesEnum.AppServer, SIPMonitorEventTypesEnum.DialPlan, "Google Voice Call timed out waiting for callback.", m_username));
                    return null;
                }
            }
            catch (Exception excp) {
                logger.Error("Exception GoogleVoiceCall SendCallRequest. " + excp.Message);
                throw;
            }
        }

        private bool MatchIncomingCall(ISIPServerUserAgent incomingCall) {
            try {
                if (incomingCall.SIPAccount.Owner != m_username) {
                    return false;
                }

                SIPHeader callHeader = incomingCall.CallRequest.Header;
                bool matchedCall = false;

                if (!m_fromURIUserRegexMatch.IsNullOrBlank()) {
                    if (Regex.Match(callHeader.From.FromURI.User, m_fromURIUserRegexMatch).Success) {
                        matchedCall = true;
                    }
                }
                else if (callHeader.UnknownHeaders.Contains("X-GoogleVoice: true") && callHeader.To.ToURI.User == m_forwardingNumber.Substring(1)) {
                    matchedCall = true;
                }

                if (matchedCall) {
                    m_callbackCall = incomingCall;
                    m_callbackCall.SetOwner(m_username, m_adminMemberId);
                    m_waitForCallback.Set();
                    return true;
                }
                else {
                    return false;
                }
            }
            catch (Exception excp) {
                logger.Error("Exception GoogleVoiceCall MatchIncomingCall. " + excp.Message);
                return false;
            }
        }
    }
}
