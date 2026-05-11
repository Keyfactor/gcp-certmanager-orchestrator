// Copyright 2025 Keyfactor
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with the License.
// You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
// Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the specific language governing permissions
// and limitations under the License.
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Google.Apis.CertificateManager.v1;
using Google.Apis.CertificateManager.v1.Data;
using Keyfactor.Extensions.Orchestrator.GcpCertManager.Client;
using Keyfactor.Logging;
using Keyfactor.Orchestrators.Common.Enums;
using Keyfactor.Orchestrators.Extensions;
using Keyfactor.Orchestrators.Extensions.Interfaces;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Pkcs;

namespace Keyfactor.Extensions.Orchestrator.GcpCertManager.Jobs
{
    public class Management : JobBase, IManagementJobExtension
    {
        private static readonly string certStart = "-----BEGIN CERTIFICATE-----\n";
        private static readonly string certEnd = "\n-----END CERTIFICATE-----";

        private const int OPERATION_MAX_WAIT_MILLISECONDS = 300000;
        private const int OPERATION_INTERVAL_WAIT_MILLISECONDS = 5000;

        private static readonly Func<string, string> Pemify = ss =>
            ss.Length <= 64 ? ss : ss.Substring(0, 64) + "\n" + Pemify(ss.Substring(64));

        protected internal virtual AsymmetricKeyEntry KeyEntry { get; set; }
        protected internal string CertificateName { get; set; }

        public Management(IPAMSecretResolver resolver) : base(resolver)
        {
            Logger = LogHandler.GetClassLogger<Management>();
        }

        public string ExtensionName => "GcpCertMgr";

        public JobResult ProcessJob(ManagementJobConfiguration jobConfiguration)
        {
            if (jobConfiguration == null)
            {
                Logger.LogError("ProcessJob called with null jobConfiguration.");
                return FailureResult(0, "ManagementJobConfiguration is null.");
            }

            using (var flow = new FlowLogger(Logger, "GcpCertMgr-Management"))
            {
                try
                {
                    Logger.MethodEntry(LogLevel.Debug);
                    return PerformManagement(jobConfiguration, flow);
                }
                catch (Exception e)
                {
                    var msg = DescribeException(e);
                    flow.Fail("ProcessJob", msg);
                    Logger.LogError(e, "Error in Management.ProcessJob: {ErrorMessage}", LogHandler.FlattenException(e));
                    return FailureResult(jobConfiguration.JobHistoryId,
                        $"Management failed: {msg}", flow);
                }
                finally
                {
                    Logger.MethodExit(LogLevel.Debug);
                }
            }
        }

        private JobResult PerformManagement(ManagementJobConfiguration config, FlowLogger flow)
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
            Logger.LogTrace("  Service Account Key Path: {ServiceAccountKey}", storeProperties.ServiceAccountKey);

            CertificateManagerService svc = null;
            flow.Step("GetGoogleCredentials", () =>
            {
                svc = new GcpCertificateManagerClient().GetGoogleCredentials(storeProperties.ServiceAccountKey);
            }, $"source={(string.IsNullOrEmpty(storeProperties.ServiceAccountKey) ? "ADC" : "file")}");

            var storePath = ResolveGcpResourcePath(
                config.CertificateStoreDetails.StorePath,
                storeProperties.ProjectId,
                storeProperties.Location);
            CertificateName = config.JobCertificate.Alias;
            flow.Step("StorePathResolved", $"storePath={storePath}, alias={CertificateName}");

            switch (config.OperationType)
            {
                case CertStoreOperationType.Add:
                    flow.Branch("Add");
                    try { return PerformAddition(svc, config, storePath, flow); }
                    finally { flow.EndBranch(); }
                case CertStoreOperationType.Remove:
                    flow.Branch("Remove");
                    try { return PerformRemoval(svc, config, storePath, flow); }
                    finally { flow.EndBranch(); }
                default:
                    flow.Fail("OperationType", $"unsupported: {config.OperationType}");
                    return FailureResult(config.JobHistoryId, "Invalid Management Operation", flow);
            }
        }

        private JobResult PerformRemoval(CertificateManagerService svc, ManagementJobConfiguration config,
            string storePath, FlowLogger flow)
        {
            flow.Step("DeleteCertificate", () => DeleteCertificate(CertificateName, svc, storePath, flow));
            flow.Step("Result", "SUCCESS - certificate removed");
            return SuccessResult(config.JobHistoryId, flow.GetSummary());
        }

        private JobResult PerformAddition(CertificateManagerService svc, ManagementJobConfiguration config,
            string storePath, FlowLogger flow)
        {
            // Validate the alias before any API calls or PFX parsing - GCP rejects
            // non-conforming IDs with HTTP 400 after we've already done the expensive
            // work, so failing fast saves both time and a confusing error message.
            flow.Step("ValidateAlias", () => ValidateGcpCertificateId(CertificateName),
                $"alias={CertificateName}");

            // Resolve the per-entry Scope entry parameter up front. Scope is per-cert
            // (not per-store) because GCP itself allows mixed-scope certs inside the same
            // (project, location). The value lands in config.JobProperties from the
            // Command UI dropdown; on renewals/reenrollments Keyfactor pre-fills it from
            // the cert's last-known inventory Parameters, so the cert keeps its scope
            // through its lifecycle without operator intervention.
            string configuredScope = null;
            if (config.JobProperties != null && config.JobProperties.TryGetValue("Scope", out var rawScope))
            {
                configuredScope = rawScope?.ToString();
            }
            string resolvedScope = null;
            flow.Step("ResolveScope", () => resolvedScope = ResolveScope(configuredScope),
                $"configured={configuredScope ?? "<blank>"}");

            var duplicate = false;
            flow.Step("CheckForDuplicate", () => duplicate = CheckForDuplicate(storePath, CertificateName, svc),
                $"alias={CertificateName}");
            Logger.LogTrace("Duplicate? = {Duplicate}", duplicate);

            if (duplicate && !config.Overwrite)
            {
                flow.Fail("DuplicateGuard",
                    $"alias '{config.JobCertificate.Alias}' exists; overwrite flag was not set");
                return FailureResult(config.JobHistoryId,
                    $"Duplicate alias {config.JobCertificate.Alias} found in Google Certificate Manager. To overwrite use the overwrite flag.",
                    flow);
            }

            if (string.IsNullOrWhiteSpace(config.JobCertificate.PrivateKeyPassword))
            {
                // Existing behaviour: this orchestrator only handles PFX entries with a
                // private key password. Surface this clearly rather than silently no-op.
                flow.Fail("PrivateKeyPassword", "missing - this orchestrator only supports PFX entries with a private key password");
                return FailureResult(config.JobHistoryId,
                    "Management/Add requires a PFX certificate with a private key password.", flow);
            }

            if (string.IsNullOrWhiteSpace(config.JobCertificate.Alias))
            {
                Logger.LogTrace("No Alias Found");
            }

            // Load PFX
            Pkcs12Store p = null;
            flow.Step("LoadPkcs12", () =>
            {
                var pfxBytes = Convert.FromBase64String(config.JobCertificate.Contents);
                using (var pfxBytesMemoryStream = new MemoryStream(pfxBytes))
                {
                    p = new Pkcs12Store(pfxBytesMemoryStream,
                        config.JobCertificate.PrivateKeyPassword.ToCharArray());
                }
            });

            Logger.LogTrace("Created Pkcs12Store containing Alias {Alias} Contains Alias is {Contains}",
                config.JobCertificate.Alias, p.ContainsAlias(config.JobCertificate.Alias));

            // Extract private key
            string alias = null;
            string privateKeyString = null;
            flow.Step("ExtractPrivateKey", () =>
            {
                using (var memoryStream = new MemoryStream())
                using (TextWriter streamWriter = new StreamWriter(memoryStream))
                {
                    var pemWriter = new PemWriter(streamWriter);
                    alias = p.Aliases.Cast<string>().SingleOrDefault(a => p.IsKeyEntry(a));
                    Logger.LogTrace("Alias = {Alias}", alias);
                    var publicKey = p.GetCertificate(alias).Certificate.GetPublicKey();
                    KeyEntry = p.GetKey(alias);
                    if (KeyEntry == null) throw new Exception("Unable to retrieve private key");

                    var privateKey = KeyEntry.Key;
                    var keyPair = new AsymmetricCipherKeyPair(publicKey, privateKey);

                    pemWriter.WriteObject(keyPair.Private);
                    streamWriter.Flush();
                    privateKeyString = Encoding.ASCII.GetString(memoryStream.GetBuffer()).Trim()
                        .Replace("\r", "").Replace("\0", "");
                }
            });

            var pubCertPem = Pemify(Convert.ToBase64String(p.GetCertificate(alias).Certificate.GetEncoded()));
            // Don't log private key material - only the public chain + alias.
            Logger.LogTrace("Public cert PEM extracted for alias {Alias}", alias);

            // Note: certPem includes the (decrypted) private key. It is intentionally NOT
            // logged. The variable is retained because the legacy code computed it inline;
            // the actual upload below uses pubCertPem + privateKeyString separately.
            var certPem = privateKeyString + certStart + pubCertPem + certEnd;
            _ = certPem;

            pubCertPem = $"-----BEGIN CERTIFICATE-----\r\n{pubCertPem}\r\n-----END CERTIFICATE-----";

            // Build the GCP certificate object. Don't serialize+log; that would leak the
            // private key into trace logs.
            //
            // Scope comes from the per-entry "Scope" entry parameter and is honored only
            // on Add. On Replace the patch's UpdateMask is "SelfManaged", so GCP ignores
            // every other field on the body (including Scope) - which is correct, since
            // GCP refuses to change scope on an existing cert anyway.
            var gCertificate = new Certificate
            {
                SelfManaged = new SelfManagedCertificate { PemCertificate = pubCertPem, PemPrivateKey = privateKeyString },
                Name = CertificateName,
                Description = CertificateName,
                Scope = resolvedScope
            };

            if (duplicate && config.Overwrite)
            {
                flow.Step("ReplaceCertificate", () => ReplaceCertificate(gCertificate, svc, storePath, flow));
            }
            else
            {
                flow.Step("AddCertificate", () => AddCertificate(gCertificate, svc, storePath, flow));
            }

            flow.Step("Result", duplicate ? "SUCCESS - certificate replaced" : "SUCCESS - certificate added");
            return SuccessResult(config.JobHistoryId, flow.GetSummary());
        }

        private void AddCertificate(Certificate gCertificate, CertificateManagerService svc, string storePath, FlowLogger flow)
        {
            var addCertificateRequest = svc.Projects.Locations.Certificates.Create(gCertificate, storePath);
            addCertificateRequest.CertificateId = gCertificate.Name;

            var addCertificateResponse = addCertificateRequest.Execute();
            flow.Step("WaitForOperation-Add", () => WaitForOperation(svc, addCertificateResponse.Name),
                $"operation={addCertificateResponse.Name}");

            Logger.LogTrace("Certificate Created in Google Cert Manager with Name {Name}", addCertificateResponse.Name);
        }

        private void ReplaceCertificate(Certificate gCertificate, CertificateManagerService svc, string storePath, FlowLogger flow)
        {
            var replaceCertificateRequest = svc.Projects.Locations.Certificates.Patch(gCertificate, storePath + $"/certificates/{CertificateName}");
            replaceCertificateRequest.UpdateMask = "SelfManaged";

            var replaceCertificateResponse = replaceCertificateRequest.Execute();
            flow.Step("WaitForOperation-Replace", () => WaitForOperation(svc, replaceCertificateResponse.Name),
                $"operation={replaceCertificateResponse.Name}");

            Logger.LogTrace("Certificate Replaced in Google Cert Manager with Name {Name}", replaceCertificateResponse.Name);
        }

        private void DeleteCertificate(string certificateName, CertificateManagerService svc, string storePath, FlowLogger flow)
        {
            var certificatesRequest = svc.Projects.Locations.Certificates.List(storePath);
            certificatesRequest.Filter = $"name=\"{storePath}/certificates/{certificateName}\"";

            var certificatesResponse = certificatesRequest.Execute();
            Logger.LogTrace("certificatesResponse Json {Response}", JsonConvert.SerializeObject(certificatesResponse));

            if (certificatesResponse?.Certificates?.Count > 0)
            {
                var deleteCertificateRequest =
                    svc.Projects.Locations.Certificates.Delete(storePath + $"/certificates/{certificateName}");

                var deleteCertificateResponse = deleteCertificateRequest.Execute();
                Logger.LogTrace("deleteCertificateResponse Json {Response}", JsonConvert.SerializeObject(deleteCertificateResponse));
                flow.Step("WaitForOperation-Delete", () => WaitForOperation(svc, deleteCertificateResponse.Name),
                    $"operation={deleteCertificateResponse.Name}");

                Logger.LogTrace("Deleted {Name} Certificate", deleteCertificateResponse.Name);
            }
            else
            {
                var msg = $"Certificate {certificateName} not found for {storePath}.";
                Logger.LogWarning(msg);
                throw new Exception(msg);
            }
        }

        private bool CheckForDuplicate(string path, string alias, CertificateManagerService client)
        {
            var certificatesRequest = client.Projects.Locations.Certificates.List(path);
            certificatesRequest.Filter = $"name=\"{path}/certificates/{alias}\"";

            var certificatesResponse = certificatesRequest.Execute();
            Logger.LogTrace("certificatesResponse Json {Response}", JsonConvert.SerializeObject(certificatesResponse));

            return certificatesResponse?.Certificates?.Count == 1;
        }

        private void WaitForOperation(CertificateManagerService client, string operationName)
        {
            var endTime = DateTime.Now.AddMilliseconds(OPERATION_MAX_WAIT_MILLISECONDS);
            var getRequest = client.Projects.Locations.Operations.Get(operationName);

            while (DateTime.Now < endTime)
            {
                Logger.LogTrace("Attempting WAIT for {OperationName} at {Now}.", operationName, DateTime.Now);
                var operation = getRequest.Execute();

                if (operation.Done == true)
                {
                    Logger.LogDebug("End WAIT for {OperationName}. Task DONE.", operationName);
                    return;
                }

                Thread.Sleep(OPERATION_INTERVAL_WAIT_MILLISECONDS);
            }

            throw new Exception($"{operationName} was still processing after the {OPERATION_MAX_WAIT_MILLISECONDS} millisecond maximum wait time.");
        }
    }
}
