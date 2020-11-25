﻿// <copyright file="AgentClient.cs" company="TestProject">
// Copyright 2020 TestProject (https://testproject.io)
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// </copyright>

using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using OpenQA.Selenium;
using RestSharp;
using TestProject.SDK.Internal.Exceptions;
using TestProject.SDK.Internal.Helpers;
using TestProject.SDK.Internal.Rest.Messages;
using TestProject.SDK.Internal.Tcp;

namespace TestProject.SDK.Internal.Rest
{
    /// <summary>
    /// Client used to communicate with the TestProject Agent process.
    /// </summary>
    public class AgentClient
    {
        /// <summary>
        /// Name of the environment variable that stores the development token.
        /// </summary>
        private readonly string tpDevToken = "TP_DEV_TOKEN";

        /// <summary>
        /// The remote address where the Agent is running.
        /// </summary>
        private Uri remoteAddress;

        /// <summary>
        /// The development token used to authenticate with the Agent.
        /// </summary>
        private string token;

        /// <summary>
        /// A REST client instance used to send HTTP requests to the Agent.
        /// </summary>
        private RestClient client;

        /// <summary>
        /// Configuration settings for (de-)serializing objects to / from JSON.
        /// </summary>
        private JsonSerializerSettings serializerSettings;

        /// <summary>
        /// Logger instance for this class.
        /// </summary>
        private static Logger Logger { get; set; } = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Initializes a new instance of the <see cref="AgentClient"/> class.
        /// </summary>
        /// <param name="remoteAddress">The remote address where the Agent is running.</param>
        /// <param name="token">The development token used to authenticate with the Agent.</param>
        /// <param name="capabilities">Requested driver options for the browser session.</param>
        /// <param name="reportSettings">Contains the project and job name to report to TestProject.</param>
        /// <param name="disableReports">Set to true to disable all reporting to TestProject, false otherwise.</param>
        public AgentClient(Uri remoteAddress, string token, DriverOptions capabilities, ReportSettings reportSettings, bool disableReports)
        {
            this.remoteAddress = remoteAddress; // TODO: Add proper address inferring logic

            if (token != null)
            {
                this.token = token;
            }
            else if (Environment.GetEnvironmentVariable(this.tpDevToken) != null)
            {
                this.token = Environment.GetEnvironmentVariable(this.tpDevToken);
            }
            else
            {
                throw new InvalidTokenException("No token has been provided.");
            }

            this.client = new RestClient(this.remoteAddress);
            this.client.AddDefaultHeader("Authorization", this.token);

            this.serializerSettings = CustomJsonSerializer.Populate(new JsonSerializerSettings());

            this.StartSession(reportSettings, capabilities);
        }

        private void StartSession(ReportSettings reportSettings, DriverOptions capabilities)
        {
            RestRequest startSessionRequest = new RestRequest("/api/development/session", Method.POST);  // TODO: move endpoints to their own class
            startSessionRequest.RequestFormat = DataFormat.Json;

            string json = CustomJsonSerializer.ToJson(new SessionRequest(reportSettings, capabilities), this.serializerSettings);

            startSessionRequest.AddJsonBody(json);

            IRestResponse startSessionResponse = this.client.Execute(startSessionRequest);

            if ((int)startSessionResponse.StatusCode >= 400)
            {
                this.HandleSessionStartFailure(startSessionResponse);
                return;
            }

            SessionResponse sessionResponse = CustomJsonSerializer.FromJson<SessionResponse>(startSessionResponse.Content, this.serializerSettings);

            Logger.Info($"Session [{sessionResponse.SessionId}] initialized");

            SocketManager.GetInstance().OpenSocket(this.remoteAddress.Host, sessionResponse.DevSocketPort);
        }

        private void HandleSessionStartFailure(IRestResponse startSessionResponse)
        {
            JObject responseBody = JObject.Parse(startSessionResponse.Content);
            string responseMessage = string.Empty;

            if (responseBody.ContainsKey("message"))
            {
                responseMessage = (string)responseBody.GetValue("message");
            }
            else
            {
                Logger.Error("Failed to read message from Agent response");
            }

            switch ((int)startSessionResponse.StatusCode)
            {
                case 401:
                    Logger.Error("Failed to initialize a session with the Agent - invalid developer token supplied");
                    Logger.Error("Get your developer token from https://app.testproject.io/#/integrations/sdk?lang=CSharp" +
                        " and set it in the TP_DEV_TOKEN environment variable");
                    throw new InvalidTokenException(responseMessage);
                case 404:
                    Logger.Error("Failed to initialize a session with the Agent - requested browser not found on this system");
                    throw new MissingBrowserException(responseMessage);
                case 406:
                    Logger.Error("Failed to initialize a session with the Agent - obsolete SDK version");
                    throw new ObsoleteVersionException(responseMessage);
                default:
                    Logger.Error("Failed to initialize a session with the Agent");
                    throw new AgentConnectException($"Agent responsed with status code {(int)startSessionResponse.StatusCode}: [{responseMessage}]");
            }
        }
    }
}
