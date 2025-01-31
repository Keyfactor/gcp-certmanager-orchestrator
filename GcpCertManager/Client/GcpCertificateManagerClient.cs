// Copyright 2025 Keyfactor
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with the License.
// You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
// Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the specific language governing permissions
// and limitations under the License.
using System.IO;
using System.Reflection;
using Google.Apis.Auth.OAuth2;
using Google.Apis.CertificateManager.v1;
using Google.Apis.Services;
using Google.Apis.Iam.v1;
using Google.Apis.Iam.v1.Data;
using System.Text;
using System;

using Keyfactor.Logging;
using Microsoft.Extensions.Logging;


namespace Keyfactor.Extensions.Orchestrator.GcpCertManager.Client
{
    public class GcpCertificateManagerClient
    {
        public CertificateManagerService GetGoogleCredentials(string credentialFileName)
        {
            ILogger _logger = LogHandler.GetClassLogger<CertificateManagerService>();

            //Credentials file needs to be in the same location of the executing assembly
            GoogleCredential credentials;

            if (!string.IsNullOrEmpty(credentialFileName))
            {
                _logger.LogDebug("Has credential file name");
                var strExeFilePath = Assembly.GetExecutingAssembly().Location;
                var strWorkPath = Path.GetDirectoryName(strExeFilePath);
                var strSettingsJsonFilePath = Path.Combine(strWorkPath ?? string.Empty, credentialFileName);

                var stream = new FileStream(strSettingsJsonFilePath,
                    FileMode.Open
                );

                credentials = GoogleCredential.FromStream(stream);
            }
            else
            {
                _logger.LogDebug("No credential file name");
                credentials = GoogleCredential.GetApplicationDefaultAsync().Result;
            }

            var service = new CertificateManagerService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credentials
            });

            return service;
        }

        public ServiceAccountKey CreateServiceAccountKey(string serviceAccountEmail)
        {
            GoogleCredential credential = GoogleCredential.GetApplicationDefault().CreateScoped(IamService.Scope.CloudPlatform);
            IamService service = new IamService(new IamService.Initializer
            {
                HttpClientInitializer = credential
            });

            var key = service.Projects.ServiceAccounts.Keys.Create(new CreateServiceAccountKeyRequest(), "projects/-/serviceAccounts/" + serviceAccountEmail).Execute();

            byte[] valueBytes = System.Convert.FromBase64String(key.PrivateKeyData);
            string jsonKeyContent = Encoding.UTF8.GetString(valueBytes);

            return key;
        }
    }
}