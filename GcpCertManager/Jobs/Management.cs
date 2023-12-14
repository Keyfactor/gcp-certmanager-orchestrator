using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using Google;
using Google.Apis.CertificateManager.v1;
using Google.Apis.CertificateManager.v1.Data;
using Keyfactor.Extensions.Orchestrator.GcpCertManager.Client;
using Keyfactor.Logging;
using Keyfactor.Orchestrators.Common.Enums;
using Keyfactor.Orchestrators.Extensions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.X509;
using static Org.BouncyCastle.Math.EC.ECCurve;

namespace Keyfactor.Extensions.Orchestrator.GcpCertManager.Jobs
{
    public class Management : IManagementJobExtension
    {
        private static readonly string certStart = "-----BEGIN CERTIFICATE-----\n";
        private static readonly string certEnd = "\n-----END CERTIFICATE-----";

        private const int OPERATION_MAX_WAIT_MILLISECONDS = 300000;
        private const int OPERATION_INTERVAL_WAIT_MILLISECONDS = 5000;

        private static readonly Func<string, string> Pemify = ss =>
            ss.Length <= 64 ? ss : ss.Substring(0, 64) + "\n" + Pemify(ss.Substring(64));

        private readonly ILogger<Management> _logger;

        public Management(ILogger<Management> logger)
        {
            _logger = logger;
        }

        protected internal virtual AsymmetricKeyEntry KeyEntry { get; set; }

        protected internal string CertificateName { get; set; }

        public string ExtensionName => "";


        public JobResult ProcessJob(ManagementJobConfiguration jobConfiguration)
        {
            try
            {
                _logger.MethodEntry(LogLevel.Debug);

                return PerformManagement(jobConfiguration);
            }
            catch (Exception e)
            {
                _logger.LogError($"Error Occurred in Management.ProcessJob: {LogHandler.FlattenException(e)}");
                throw;
            }
        }

        private JobResult PerformManagement(ManagementJobConfiguration config)
        {
            try
            {
                _logger.MethodEntry();

                StoreProperties storeProperties = JsonConvert.DeserializeObject<StoreProperties>(config.CertificateStoreDetails.Properties,
                    new JsonSerializerSettings { DefaultValueHandling = DefaultValueHandling.Populate });
                storeProperties.ProjectId = config.CertificateStoreDetails.ClientMachine;

                _logger.LogTrace($"Store Properties:");
                _logger.LogTrace($"  Location: {storeProperties.Location}");
                _logger.LogTrace($"  Project Id: {storeProperties.ProjectId}");
                _logger.LogTrace($"  Service Account Key Path: {storeProperties.ServiceAccountKey}");

                _logger.LogTrace("Getting Credentials from Google...");
                var svc = string.IsNullOrEmpty(storeProperties.ServiceAccountKey) ? new CertificateManagerService() : new GcpCertificateManagerClient().GetGoogleCredentials(storeProperties.ServiceAccountKey);
                _logger.LogTrace("Got Credentials from Google");

                var storePath = $"projects/{storeProperties.ProjectId}/locations/{storeProperties.Location}";
                CertificateName = config.JobCertificate.Alias;

                var complete = new JobResult
                {
                    Result = OrchestratorJobStatusJobResult.Failure,
                    JobHistoryId = config.JobHistoryId,
                    FailureMessage =
                        "Invalid Management Operation"
                };

                switch (config.OperationType)
                {
                    case CertStoreOperationType.Add:
                        _logger.LogTrace("Adding...");
                        complete = PerformAddition(svc, config, storePath);
                        break;
                    case CertStoreOperationType.Remove:
                        _logger.LogTrace("Removing...");
                        complete = PerformRemoval(svc, config, storePath);
                        break;
                    default:
                        return complete;
                }

                _logger.MethodExit();
                return complete;
            }
            catch (GoogleApiException e)
            {
                var googleError = e.Error?.ErrorResponseContent + " " + LogHandler.FlattenException(e);
                return new JobResult
                {
                    Result = OrchestratorJobStatusJobResult.Failure,
                    JobHistoryId = config.JobHistoryId,
                    FailureMessage =
                        $"Management {googleError}"
                };
            }
            catch (Exception e)
            {
                _logger.LogError($"Error Occurred in Management.PerformManagement: {LogHandler.FlattenException(e)}");
                throw;
            }
        }


        private JobResult PerformRemoval(CertificateManagerService svc, ManagementJobConfiguration config, string storePath)
        {
            try
            {
                _logger.MethodEntry();

                DeleteCertificate(CertificateName, svc, storePath);

                _logger.MethodExit();
                return new JobResult
                {
                    Result = OrchestratorJobStatusJobResult.Success,
                    JobHistoryId = config.JobHistoryId,
                    FailureMessage = ""
                };
            }
            catch (GoogleApiException e)
            {
                var googleError = e.Error?.ErrorResponseContent + " " + LogHandler.FlattenException(e);
                return new JobResult
                {
                    Result = OrchestratorJobStatusJobResult.Failure,
                    JobHistoryId = config.JobHistoryId,
                    FailureMessage =
                        $"Management/Remove {googleError}"
                };
            }
            catch (Exception e)
            {
                return new JobResult
                {
                    Result = OrchestratorJobStatusJobResult.Failure,
                    JobHistoryId = config.JobHistoryId,
                    FailureMessage = $"Management/Remove: {LogHandler.FlattenException(e)}"
                };
            }
        }


        private JobResult PerformAddition(CertificateManagerService svc, ManagementJobConfiguration config, string storePath)
        {
            //Temporarily only performing additions
            try
            {
                _logger.MethodEntry();

                var client = new GcpCertificateManagerClient();

                var duplicate = CheckForDuplicate(storePath, CertificateName, svc);
                _logger.LogTrace($"Duplicate? = {duplicate}");

                //Check for Duplicate already in Google Certificate Manager, if there, make sure the Overwrite flag is checked before replacing
                if (duplicate && config.Overwrite || !duplicate)
                {
                    _logger.LogTrace("Either not a duplicate or overwrite was chosen....");
                    if (!string.IsNullOrWhiteSpace(config.JobCertificate.PrivateKeyPassword)) // This is a PFX Entry
                    {

                        if (string.IsNullOrWhiteSpace(config.JobCertificate.Alias))
                            _logger.LogTrace("No Alias Found");

                        // Load PFX
                        var pfxBytes = Convert.FromBase64String(config.JobCertificate.Contents);
                        Pkcs12Store p;
                        using (var pfxBytesMemoryStream = new MemoryStream(pfxBytes))
                        {
                            p = new Pkcs12Store(pfxBytesMemoryStream,
                                config.JobCertificate.PrivateKeyPassword.ToCharArray());
                        }

                        _logger.LogTrace(
                            $"Created Pkcs12Store containing Alias {config.JobCertificate.Alias} Contains Alias is {p.ContainsAlias(config.JobCertificate.Alias)}");

                        // Extract private key
                        string alias;
                        string privateKeyString;
                        using (var memoryStream = new MemoryStream())
                        {
                            using (TextWriter streamWriter = new StreamWriter(memoryStream))
                            {
                                _logger.LogTrace("Extracting Private Key...");
                                var pemWriter = new PemWriter(streamWriter);
                                _logger.LogTrace("Created pemWriter...");
                                alias = p.Aliases.Cast<string>().SingleOrDefault(a => p.IsKeyEntry(a));
                                _logger.LogTrace($"Alias = {alias}");
                                var publicKey = p.GetCertificate(alias).Certificate.GetPublicKey();
                                _logger.LogTrace($"publicKey = {publicKey}");
                                KeyEntry = p.GetKey(alias);
                                _logger.LogTrace($"KeyEntry = {KeyEntry}");
                                if (KeyEntry == null) throw new Exception("Unable to retrieve private key");

                                var privateKey = KeyEntry.Key;
                                var keyPair = new AsymmetricCipherKeyPair(publicKey, privateKey);

                                pemWriter.WriteObject(keyPair.Private);
                                streamWriter.Flush();
                                privateKeyString = Encoding.ASCII.GetString(memoryStream.GetBuffer()).Trim()
                                    .Replace("\r", "").Replace("\0", "");
                                memoryStream.Close();
                                streamWriter.Close();
                                _logger.LogTrace("Finished Extracting Private Key...");
                            }
                        }

                        var pubCertPem =
                            Pemify(Convert.ToBase64String(p.GetCertificate(alias).Certificate.GetEncoded()));
                        _logger.LogTrace($"Public cert Pem {pubCertPem}");

                        var certPem = privateKeyString + certStart + pubCertPem + certEnd;

                        _logger.LogTrace($"Got certPem {certPem}");

                        pubCertPem = $"-----BEGIN CERTIFICATE-----\r\n{pubCertPem}\r\n-----END CERTIFICATE-----";

                        _logger.LogTrace($"Public Cert Pem: {pubCertPem}");

                        //Create the certificate in Google
                        var gCertificate = new Certificate
                        {
                            SelfManaged = new SelfManagedCertificate
                            { PemCertificate = pubCertPem, PemPrivateKey = privateKeyString },
                            Name = CertificateName,
                            Description = CertificateName,
                            Scope = "DEFAULT" //Scope does not come back in inventory so just hard code it for now
                        };

                        _logger.LogTrace(
                            $"Created Google Certificate Object: {JsonConvert.SerializeObject(gCertificate)}");

                        if (duplicate && config.Overwrite)
                            ReplaceCertificate(gCertificate, svc, storePath);
                        else
                            AddCertificate(gCertificate, svc, storePath);

                        _logger.MethodExit();

                        //Return success from job
                        return new JobResult
                        {
                            Result = OrchestratorJobStatusJobResult.Success,
                            JobHistoryId = config.JobHistoryId,
                            FailureMessage = ""
                        };
                    }
                }
                _logger.MethodExit();
                return new JobResult
                {
                    Result = OrchestratorJobStatusJobResult.Failure,
                    JobHistoryId = config.JobHistoryId,
                    FailureMessage =
                        $"Duplicate alias {config.JobCertificate.Alias} found in Google Certificate Manager.  To overwrite use the overwrite flag."
                };
            }
            catch (GoogleApiException e)
            {
                var googleError = e.Error?.ErrorResponseContent + " " + LogHandler.FlattenException(e);
                return new JobResult
                {
                    Result = OrchestratorJobStatusJobResult.Failure,
                    JobHistoryId = config.JobHistoryId,
                    FailureMessage =
                        $"Management/Add {googleError}"
                };
            }
            catch (Exception e)
            {
                return new JobResult
                {
                    Result = OrchestratorJobStatusJobResult.Failure,
                    JobHistoryId = config.JobHistoryId,
                    FailureMessage = $"Management/Add {LogHandler.FlattenException(e)}"
                };
            }
        }

        private void AddCertificate(Certificate gCertificate, CertificateManagerService svc, string storePath)
        {
            var addCertificateRequest = svc.Projects.Locations.Certificates.Create(gCertificate, storePath);
            addCertificateRequest.CertificateId = gCertificate.Name;

            var addCertificateResponse = addCertificateRequest.Execute();
            WaitForOperation(svc, addCertificateResponse.Name);

            _logger.LogTrace($"Certificate Created in Google Cert Manager with Name {addCertificateResponse.Name}");

            _logger.MethodExit();
        }

        private void ReplaceCertificate(Certificate gCertificate, CertificateManagerService svc, string storePath)
        {
            _logger.MethodEntry();

            var replaceCertificateRequest = svc.Projects.Locations.Certificates.Patch(gCertificate, storePath + $"/certificates/{CertificateName}");
            replaceCertificateRequest.UpdateMask = "SelfManaged";

            var replaceCertificateResponse = replaceCertificateRequest.Execute();
            WaitForOperation(svc, replaceCertificateResponse.Name);

            _logger.LogTrace($"Certificate Replaced in Google Cert Manager with Name {replaceCertificateResponse.Name}");

            _logger.MethodExit();
        }

        private void DeleteCertificate(string certificateName,
            CertificateManagerService svc, string storePath)
        {
            try
            {
                _logger.MethodEntry();

                var certificatesRequest = svc.Projects.Locations.Certificates.List(storePath);
                certificatesRequest.Filter = $"name=\"{storePath}/certificates/{certificateName}\"";

                var certificatesResponse = certificatesRequest.Execute();
                _logger.LogTrace($"certificatesResponse Json {JsonConvert.SerializeObject(certificatesResponse)}");

                if (certificatesResponse?.Certificates?.Count > 0)
                {
                    var deleteCertificateRequest =
                        svc.Projects.Locations.Certificates.Delete(storePath + $"/certificates/{certificateName}");

                    var deleteCertificateResponse = deleteCertificateRequest.Execute();
                    _logger.LogTrace(
                        $"deleteCertificateResponse Json {JsonConvert.SerializeObject(deleteCertificateResponse)}");
                    WaitForOperation(svc, deleteCertificateResponse.Name);

                    _logger.LogTrace($"Deleted {deleteCertificateResponse.Name} Certificate During Replace Procedure");
                }
                else
                {
                    string msg = $"Certificate {certificateName} not found for {storePath}.";
                    _logger.LogWarning(msg);
                    throw new Exception(msg);
                }

                _logger.MethodExit();
            }
            catch (Exception e)
            {
                _logger.LogError($"Error occured in Management.DeleteCertificate: {LogHandler.FlattenException(e)}");
                throw;
            }
        }

        private bool CheckForDuplicate(string path, string alias, CertificateManagerService client)
        {
            try
            {
                _logger.MethodEntry();
                var certificatesRequest =
                    client.Projects.Locations.Certificates.List(path);
                certificatesRequest.Filter = $"name=\"{path}/certificates/{alias}\"";

                var certificatesResponse = certificatesRequest.Execute();
                _logger.LogTrace($"certificatesResponse Json {JsonConvert.SerializeObject(certificatesResponse)}");

                if (certificatesResponse?.Certificates?.Count == 1)
                {
                    _logger.MethodExit();
                    return true;
                }

                _logger.MethodExit();
                return false;
            }
            catch (Exception e)
            {
                _logger.LogError(
                    $"Error Checking for Duplicate Cert in Management.CheckForDuplicate {LogHandler.FlattenException(e)}");
                throw;
            }
        }

        private void WaitForOperation(CertificateManagerService client, string operationName)
        {
            _logger.MethodEntry();

            DateTime endTime = DateTime.Now.AddMilliseconds(OPERATION_MAX_WAIT_MILLISECONDS);
            Operation operation = new Operation();
            ProjectsResource.LocationsResource.OperationsResource.GetRequest getRequest = client.Projects.Locations.Operations.Get(operationName);

            while (DateTime.Now < endTime)
            {
                _logger.LogTrace($"Attempting WAIT for {operationName} at {DateTime.Now.ToString()}.");
                operation = getRequest.Execute();

                if (operation.Done == true)
                {
                    _logger.LogDebug($"End WAIT for {operationName}. Task DONE.");
                    _logger.MethodExit();
                    return;
                }

                System.Threading.Thread.Sleep(OPERATION_INTERVAL_WAIT_MILLISECONDS);
            }

            _logger.MethodExit();
            throw new Exception($"{operationName} was still processing after the {OPERATION_MAX_WAIT_MILLISECONDS.ToString()} millisecond maximum wait time.");
        }

        private string GetCommonNameFromSubject(string subject)
        {
            try
            {
                _logger.MethodEntry();
                var array1 = subject.Split(',');
                foreach (var x in array1)
                {
                    var itemArray = x.Split('=');

                    switch (itemArray[0].ToUpper())
                    {
                        case "CN":
                            return itemArray[1];
                    }
                }

                _logger.LogTrace("Could not get subject returning empty string...");
                _logger.MethodExit();
                return "";
            }
            catch (Exception e)
            {
                _logger.LogError(
                    $"Error Checking for Duplicate Cert in Management.GetCommonNameFromSubject {LogHandler.FlattenException(e)}");
                throw;
            }
        }
    }
}