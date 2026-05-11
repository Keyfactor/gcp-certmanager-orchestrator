// Copyright 2025 Keyfactor
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with the License.
// You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
// Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the specific language governing permissions
// and limitations under the License.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Google.Apis.CertificateManager.v1;
using Google.Apis.CertificateManager.v1.Data;
using Keyfactor.Extensions.Orchestrator.GcpCertManager.Client;
using Keyfactor.Logging;
using Keyfactor.Orchestrators.Common.Enums;
using Keyfactor.Orchestrators.Extensions;
using Keyfactor.Orchestrators.Extensions.Interfaces;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Keyfactor.Extensions.Orchestrator.GcpCertManager.Jobs
{
    public class Inventory : JobBase, IInventoryJobExtension
    {
        public Inventory(IPAMSecretResolver resolver) : base(resolver)
        {
            Logger = LogHandler.GetClassLogger<Inventory>();
        }

        public string ExtensionName => "GcpCertMgr";

        public JobResult ProcessJob(InventoryJobConfiguration jobConfiguration,
            SubmitInventoryUpdate submitInventoryUpdate)
        {
            if (jobConfiguration == null)
            {
                Logger.LogError("ProcessJob called with null jobConfiguration.");
                return FailureResult(0, "InventoryJobConfiguration is null.");
            }

            if (submitInventoryUpdate == null)
            {
                Logger.LogError("ProcessJob called with null submitInventoryUpdate.");
                return FailureResult(jobConfiguration.JobHistoryId, "SubmitInventoryUpdate delegate is null.");
            }

            using (var flow = new FlowLogger(Logger, "GcpCertMgr-Inventory"))
            {
                try
                {
                    Logger.MethodEntry(LogLevel.Debug);
                    return PerformInventory(jobConfiguration, submitInventoryUpdate, flow);
                }
                catch (Exception e)
                {
                    var msg = DescribeException(e);
                    flow.Fail("ProcessJob", msg);
                    Logger.LogError(e, "Error in Inventory.ProcessJob: {ErrorMessage}", LogHandler.FlattenException(e));
                    return FailureResult(jobConfiguration.JobHistoryId,
                        $"Inventory failed: {msg}", flow);
                }
                finally
                {
                    Logger.MethodExit(LogLevel.Debug);
                }
            }
        }

        private JobResult PerformInventory(InventoryJobConfiguration config,
            SubmitInventoryUpdate submitInventory, FlowLogger flow)
        {
            StoreProperties storeProperties = null;
            flow.Step("ParseStoreProperties", () =>
            {
                storeProperties = JsonConvert.DeserializeObject<StoreProperties>(
                    config.CertificateStoreDetails.Properties,
                    new JsonSerializerSettings { DefaultValueHandling = DefaultValueHandling.Populate });
                storeProperties.ProjectId = config.CertificateStoreDetails.ClientMachine;
            }, $"projectId={config.CertificateStoreDetails.ClientMachine}");

            Logger.LogTrace("Store Properties:");
            Logger.LogTrace("  Location: {Location}", storeProperties.Location);
            Logger.LogTrace("  Project Id: {ProjectId}", storeProperties.ProjectId);
            // ServiceAccountKey is a file PATH, not a secret value, so it is fine to log.
            Logger.LogTrace("  Service Account Key Path: {ServiceAccountKey}", storeProperties.ServiceAccountKey);

            CertificateManagerService svc = null;
            flow.Step("GetGoogleCredentials", () =>
            {
                svc = new GcpCertificateManagerClient().GetGoogleCredentials(storeProperties.ServiceAccountKey);
            }, $"source={(string.IsNullOrEmpty(storeProperties.ServiceAccountKey) ? "ADC" : "file")}");

            var warningFlag = false;
            var sb = new StringBuilder();
            var inventoryItems = new List<CurrentInventoryItem>();
            var nextPageToken = string.Empty;
            var storePath = ResolveGcpResourcePath(
                config.CertificateStoreDetails.StorePath,
                storeProperties.ProjectId,
                storeProperties.Location);

            flow.Step("StorePathResolved", $"storePath={storePath}");

            var pageCount = 0;
            do
            {
                pageCount++;
                ListCertificatesResponse certificatesResponse = null;
                var token = nextPageToken;
                flow.Step($"ListCertificates-page{pageCount}", () =>
                {
                    var certificatesRequest = svc.Projects.Locations.Certificates.List(storePath);
                    certificatesRequest.Filter = "pemCertificate!=\"\"";
                    certificatesRequest.PageSize = 100;
                    if (!string.IsNullOrEmpty(token)) certificatesRequest.PageToken = token;

                    certificatesResponse = certificatesRequest.Execute();
                });

                Logger.LogTrace("certificatesResponse: {Response}", JsonConvert.SerializeObject(certificatesResponse));

                nextPageToken = null;
                if (certificatesResponse?.Certificates != null)
                {
                    foreach (var c in certificatesResponse.Certificates)
                    {
                        try
                        {
                            Logger.LogTrace(
                                "Building Cert List Inventory Item Alias: {Name} Pem: {Pem} Private Key: dummy (from PA API)",
                                c.Name, c.PemCertificate);
                            var item = BuildInventoryItem(c.Name, c.PemCertificate, true, storePath, svc, c.Scope);
                            if (item?.Certificates != null)
                                inventoryItems.Add(item);
                        }
                        catch (Exception inner)
                        {
                            Logger.LogWarning("Could not fetch the certificate: {Name} associated with description {Description}. {Error}",
                                c?.Name, c?.Description, DescribeException(inner));
                            sb.AppendLine($"Could not fetch the certificate: {c?.Name} associated with issuer {c?.Description}.");
                            warningFlag = true;
                        }
                    }
                }

                nextPageToken = certificatesResponse?.NextPageToken;
            } while (!string.IsNullOrEmpty(nextPageToken));

            flow.Step("SubmitInventory", () => submitInventory.Invoke(inventoryItems),
                $"itemCount={inventoryItems.Count}");

            // Per the playbook: append the flow summary on BOTH success and warning paths
            // so operators reading job history can see what happened either way.
            var summary = flow.GetSummary();

            if (warningFlag)
            {
                flow.Step("Result", "WARNING - some certificates could not be fetched");
                return WarningResult(config.JobHistoryId, $"{sb}\n\n{summary}");
            }

            flow.Step("Result", $"SUCCESS - {inventoryItems.Count} certificates");
            return SuccessResult(config.JobHistoryId, summary);
        }

        protected virtual CurrentInventoryItem BuildInventoryItem(string alias, string certPem, bool privateKey,
            string storePath, CertificateManagerService svc, string scope)
        {
            try
            {
                Logger.MethodEntry();
                Logger.LogTrace("Alias: {Alias} Pem: {Pem} PrivateKey: {PrivateKey} Scope: {Scope}",
                    alias, certPem, privateKey, scope ?? "<null>");

                var certAttributes = GetCertificateAttributes(storePath);
                // GCP omits the scope field from the response when it's the default.
                // Normalize null/blank to "DEFAULT" here so Command's UI always shows a
                // concrete value on inventoried certs - and so that renewal jobs land a
                // non-null Scope in JobProperties when Keyfactor replays the entry params.
                certAttributes["Scope"] = string.IsNullOrWhiteSpace(scope) ? "DEFAULT" : scope;
                var modAlias = alias.Split('/')[5];
                Logger.LogTrace("Got modAlias: {ModAlias}", modAlias);

                var acsi = new CurrentInventoryItem
                {
                    Alias = modAlias,
                    Certificates = new[] { certPem },
                    ItemStatus = OrchestratorInventoryItemStatus.Unknown,
                    PrivateKeyEntry = privateKey,
                    UseChainLevel = false,
                    Parameters = certAttributes
                };

                Logger.MethodExit();
                return acsi;
            }
            catch (Exception e)
            {
                Logger.LogError("Error Occurred in Inventory.BuildInventoryItem: {Error}", LogHandler.FlattenException(e));
                throw;
            }
        }

        protected Dictionary<string, object> GetCertificateAttributes(string storePath)
        {
            try
            {
                Logger.MethodEntry();
                Logger.LogTrace("Store Path: {StorePath}", storePath);
                var locationName = storePath.Split('/')[3];

                var siteSettingsDict = new Dictionary<string, object>
                {
                    { "Location", locationName }
                };

                Logger.MethodExit();
                return siteSettingsDict;
            }
            catch (Exception e)
            {
                Logger.LogError("Error Occurred in Inventory.GetCertificateAttributes: {Error}", LogHandler.FlattenException(e));
                throw;
            }
        }
    }
}
