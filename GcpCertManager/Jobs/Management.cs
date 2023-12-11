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

        protected internal string MapName { get; set; }

        protected internal string MapEntryName { get; set; }

        protected internal string CertificateName { get; set; }

        public string ExtensionName => "GcpCertManager";


        public JobResult ProcessJob(ManagementJobConfiguration jobConfiguration)
        {
            try
            {
                _logger.MethodEntry();
                MapName = GetMapSettingsFromAlias(jobConfiguration.JobCertificate.Alias, "map");
                _logger.LogTrace($"MapName: {MapName}");
                MapEntryName = GetMapSettingsFromAlias(jobConfiguration.JobCertificate.Alias, "mapentry");
                _logger.LogTrace($"MapEntryName: {MapEntryName}");
                CertificateName = GetMapSettingsFromAlias(jobConfiguration.JobCertificate.Alias, "certificate");
                _logger.LogTrace($"CertificateName: {CertificateName}");
                _logger.MethodExit();
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
                var complete = new JobResult
                {
                    Result = OrchestratorJobStatusJobResult.Failure,
                    JobHistoryId = config.JobHistoryId,
                    FailureMessage =
                        "Invalid Management Operation"
                };

                if (config.OperationType.ToString() == "Add")
                {
                    _logger.LogTrace("Adding...");
                    _logger.LogTrace($"Add Config Json {JsonConvert.SerializeObject(config)}");
                    complete = PerformAddition(config);
                }
                else if (config.OperationType.ToString() == "Remove")
                {
                    _logger.LogTrace("Removing...");
                    _logger.LogTrace($"Remove Config Json {JsonConvert.SerializeObject(config)}");
                    complete = PerformRemoval(config);
                }

                _logger.MethodExit();
                return complete;
            }
            catch (GoogleApiException e)
            {
                var googleError = e.Error.ErrorResponseContent;
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


        private JobResult PerformRemoval(ManagementJobConfiguration config)
        {
            try
            {
                _logger.MethodEntry();

                _logger.LogTrace(
                    $"Credentials JSON: Url: {config.CertificateStoreDetails.ClientMachine} Password: {config.ServerPassword}");

                var storeProps = JsonConvert.DeserializeObject<StorePath>(config.CertificateStoreDetails.Properties,
                    new JsonSerializerSettings {DefaultValueHandling = DefaultValueHandling.Populate});
                _logger.LogTrace($"Store Properties: {JsonConvert.SerializeObject(storeProps)}");
                if (storeProps != null)
                {
                    var location = storeProps.Location;
                    var storePath = $"projects/{config.CertificateStoreDetails.StorePath}/locations/{location}";
                    var client = new GcpCertificateManagerClient();
                    _logger.LogTrace("Getting Credentials from Google...");
                    var svc = client.GetGoogleCredentials(config.CertificateStoreDetails.ClientMachine);
                    _logger.LogTrace($"Got Credentials from Google");

                    DeleteCertificate(CertificateName, svc, storePath);
                }

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
                var googleError = e.Error.ErrorResponseContent;
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
                    FailureMessage = $"PerformRemoval: {LogHandler.FlattenException(e)}"
                };
            }
        }


        private JobResult PerformAddition(ManagementJobConfiguration config)
        {
            //Temporarily only performing additions
            try
            {
                _logger.MethodEntry();

                _logger.LogTrace(
                    $"Credentials JSON: Url: {config.CertificateStoreDetails.ClientMachine} Password: {config.ServerPassword}");

                var storeProps = JsonConvert.DeserializeObject<StorePath>(config.CertificateStoreDetails.Properties,
                    new JsonSerializerSettings {DefaultValueHandling = DefaultValueHandling.Populate});
                _logger.LogTrace($"Store Properties: {JsonConvert.SerializeObject(storeProps)}");

                if (storeProps != null)
                {
                    var location = storeProps.Location;
                    var storePath = $"projects/{config.CertificateStoreDetails.StorePath}/locations/{location}";
                    var client = new GcpCertificateManagerClient();
                    _logger.LogTrace("Getting Credentials from Google...");
                    var svc = client.GetGoogleCredentials(config.CertificateStoreDetails.ClientMachine);
                    _logger.LogTrace($"Got Credentials from Google");

                    var duplicate = CheckForDuplicate(storePath, CertificateName, svc);
                    _logger.LogTrace($"Duplicate? = {duplicate}");

                    //Check for Duplicate already in Google Certificate Manager, if there, make sure the Overwrite flag is checked before replacing
                    if (duplicate && config.Overwrite || !duplicate)
                    {
                        _logger.LogTrace("Either not a duplicate or overwrite was chosen....");
                        if (!string.IsNullOrWhiteSpace(config.JobCertificate.PrivateKeyPassword)) // This is a PFX Entry
                        {
                            _logger.LogTrace($"Found Private Key {config.JobCertificate.PrivateKeyPassword}");

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
                                    _logger.LogTrace($"privateKey = {privateKey}");
                                    var keyPair = new AsymmetricCipherKeyPair(publicKey, privateKey);

                                    pemWriter.WriteObject(keyPair.Private);
                                    streamWriter.Flush();
                                    privateKeyString = Encoding.ASCII.GetString(memoryStream.GetBuffer()).Trim()
                                        .Replace("\r", "").Replace("\0", "");
                                    _logger.LogTrace($"Got Private Key String {privateKeyString}");
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


                            if (MapName.Length > 0 && MapEntryName.Length > 0)
                            {
                                var mapCreated = CreateMap(MapName, svc, storePath);
                                if (mapCreated == null)
                                    return new JobResult
                                    {
                                        Result = OrchestratorJobStatusJobResult.Failure,
                                        JobHistoryId = config.JobHistoryId,
                                        FailureMessage = $"Could not create the certificate map Named: {MapName}"
                                    };
                                _logger.LogTrace($"Certificate Map Created with Name {mapCreated.Name}");
                            }

                            pubCertPem = $"-----BEGIN CERTIFICATE-----\r\n{pubCertPem}\r\n-----END CERTIFICATE-----";

                            _logger.LogTrace($"Public Cert Pem: {pubCertPem}");

                            //2. Create the certificate in Google
                            var gCertificate = new Certificate
                            {
                                SelfManaged = new SelfManagedCertificate
                                    {PemCertificate = pubCertPem, PemPrivateKey = privateKeyString},
                                Name = CertificateName,
                                Description = CertificateName,
                                Scope = "DEFAULT" //Scope does not come back in inventory so just hard code it for now
                            };

                            _logger.LogTrace(
                                $"Created Google Certificate Object: {JsonConvert.SerializeObject(gCertificate)}");

                            X509Certificate replaceCertificateResponse;
                            if (duplicate && config.Overwrite)
                                replaceCertificateResponse = ReplaceCertificate(gCertificate, svc, storePath, true);
                            else
                                replaceCertificateResponse =
                                    ReplaceCertificate(gCertificate, svc, storePath, false);

                            _logger.LogTrace(
                                $"Certificate Created with SubjectDn {replaceCertificateResponse.SubjectDN}");

                            if (MapName.Length > 0 && MapEntryName.Length > 0)
                            {
                                _logger.LogTrace("Found Map Entry and Map...");
                                //Get the host name to be passed into the create map call
                                var subject = GetCommonNameFromSubject(replaceCertificateResponse.SubjectDN.ToString());
                                _logger.LogTrace($"Got Subject: {subject}");

                                var createCertificateMapEntryBody = new CertificateMapEntry
                                {
                                    Name = MapEntryName,
                                    Description = MapEntryName,
                                    Hostname = subject,
                                    Certificates = new List<string> {$"{storePath}/certificates/{gCertificate.Name}"}
                                };

                                _logger.LogTrace(
                                    $"Created Certificate Map Entry Body: {JsonConvert.SerializeObject(createCertificateMapEntryBody)}");

                                //4. Check for Existing Map with the same name if matches the Entry Param then use it if not create new
                                var mapEntryCreated = CreateMapEntry(createCertificateMapEntryBody, svc,
                                    storePath + "/certificateMaps/" + MapName);
                                _logger.LogTrace($"Certificate Map Entry Created with Name {mapEntryCreated.Name}");
                            }

                            //5. Return success from job
                            return new JobResult
                            {
                                Result = OrchestratorJobStatusJobResult.Success,
                                JobHistoryId = config.JobHistoryId,
                                FailureMessage = ""
                            };
                        }
                    }
                }

                _logger.MethodExit();
                return new JobResult
                {
                    Result = OrchestratorJobStatusJobResult.Failure,
                    JobHistoryId = config.JobHistoryId,
                    FailureMessage =
                        $"Duplicate alias {config.JobCertificate.Alias} found in Google Certificate Manager, to overwrite use the overwrite flag."
                };
            }
            catch (GoogleApiException e)
            {
                var googleError = e.Error.ErrorResponseContent;
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

        private X509Certificate ReplaceCertificate(Certificate gCertificate,
            CertificateManagerService svc, string storePath, bool overwrite)
        {
            try
            {
                _logger.MethodEntry();
                //DEFAULT or EDGE_CACHE
                //todo add labels 
                //Path does not support cert and private key replacement so delete and insert instead
                if (overwrite) DeleteCertificate(gCertificate.Name, svc, storePath);

                var replaceCertificateRequest = svc.Projects.Locations.Certificates.Create(gCertificate, storePath);
                replaceCertificateRequest.CertificateId = gCertificate.Name;
                var replaceCertificateResponse = replaceCertificateRequest.Execute();
                WaitForOperation(svc, replaceCertificateResponse.Name);

                _logger.LogTrace(
                    $"Certificate Created in Google Cert Manager with Name {replaceCertificateResponse.Name}");

                var pemString = gCertificate.SelfManaged.PemCertificate;
                pemString = pemString.Replace("-----BEGIN CERTIFICATE-----", "")
                    .Replace("-----END CERTIFICATE-----", "");
                var buffer = Convert.FromBase64String(pemString);
                var parser = new X509CertificateParser();
                var cert = parser.ReadCertificate(buffer);

                _logger.LogTrace($"X509 Serialized: {cert}");

                _logger.MethodExit();
                return cert;
            }
            catch (Exception e)
            {
                _logger.LogError($"Error occured in Management.CreateCertificate: {LogHandler.FlattenException(e)}");
                throw;
            }
        }

        private void DeleteCertificate(string certificateName,
            CertificateManagerService svc, string storePath)
        {
            try
            {
                _logger.MethodEntry();
                if (MapName.Length > 0)
                {
                    //See if map entry exists, if so delete it
                    var certificateMapEntryListRequest =
                        svc.Projects.Locations.CertificateMaps.CertificateMapEntries.List(storePath +
                            $"/certificateMaps/{MapName}");
                    certificateMapEntryListRequest.Filter =
                        $"name=\"{storePath}/certificateMaps/{MapName}/certificateMapEntries/{MapEntryName}\"";

                    var certificateMapEntryListResponse = certificateMapEntryListRequest.Execute();
                    _logger.LogTrace(
                        $"Map Entry Response Json {JsonConvert.SerializeObject(certificateMapEntryListResponse)}");

                    if (certificateMapEntryListResponse?.CertificateMapEntries?.Count > 0)
                    {
                        var deleteCertificateMapEntryRequest =
                            svc.Projects.Locations.CertificateMaps.CertificateMapEntries.Delete(storePath +
                                $"/certificateMaps/{MapName}/certificateMapEntries/{MapEntryName}");

                        var deleteCertificateMapEntryResponse = deleteCertificateMapEntryRequest.Execute();
                        _logger.LogTrace(
                            $"Delete Certificate Response Json {JsonConvert.SerializeObject(deleteCertificateMapEntryResponse)}");
                        WaitForOperation(svc, deleteCertificateMapEntryResponse.Name);

                        _logger.LogTrace(
                            $"Deleted {deleteCertificateMapEntryResponse.Name} Certificate Map Entry During Replace Procedure");
                    }
                }

                var certificatesRequest = svc.Projects.Locations.Certificates.List(storePath);
                certificatesRequest.Filter = $"name=\"{storePath}/certificates/{certificateName}\"";

                var certificatesResponse = certificatesRequest.Execute();
                _logger.LogTrace($"certificatesResponse Json {JsonConvert.SerializeObject(certificatesResponse)}");

                if (certificatesResponse?.Certificates?.Count > 0)
                {
                    var deleteCertificateRequest =
                        svc.Projects.Locations.Certificates.Delete(storePath + $"/certificates/{certificateName}");

                    var deleteCertificateResponse = deleteCertificateRequest.Execute();
                    WaitForOperation(svc, deleteCertificateResponse.Name);
                    _logger.LogTrace(
                        $"deleteCertificateResponse Json {JsonConvert.SerializeObject(deleteCertificateResponse)}");

                    _logger.LogTrace($"Deleted {deleteCertificateResponse.Name} Certificate During Replace Procedure");
                }

                _logger.MethodExit();
            }
            catch (Exception e)
            {
                _logger.LogError($"Error occured in Management.DeleteCertificate: {LogHandler.FlattenException(e)}");
                throw;
            }
        }

        private CertificateMap CreateMap(string mapName, CertificateManagerService client, string parent)
        {
            try
            {
                _logger.MethodEntry();
                var certificateMapListRequest = client.Projects.Locations.CertificateMaps.List(parent);
                var mapFilter = $"{parent}/certificateMaps/{mapName}";
                certificateMapListRequest.Filter = $"name=\"{mapFilter}\"";

                var certificateMapListResponse = certificateMapListRequest.Execute();
                _logger.LogTrace(
                    $"certificateMapListResponse Json {JsonConvert.SerializeObject(certificateMapListResponse)}");

                if (certificateMapListResponse?.CertificateMaps?.Count > 0)
                {
                    _logger.MethodExit();
                    return certificateMapListResponse.CertificateMaps[0];
                }

                var certificateMapBody = new CertificateMap {Name = mapName, Description = mapName};
                var certificateMapCreateRequest =
                    client.Projects.Locations.CertificateMaps.Create(certificateMapBody, parent);
                certificateMapCreateRequest.CertificateMapId = mapName;

                var certificateMapCreateResponse = certificateMapCreateRequest.Execute();
                WaitForOperation(client, certificateMapCreateResponse.Name);
                _logger.LogTrace(
                    $"certificateMapCreateResponse Json {JsonConvert.SerializeObject(certificateMapCreateResponse)}");

                if (certificateMapCreateResponse?.Name?.Length > 0)
                {
                    var certificateMapRequest =
                        client.Projects.Locations.CertificateMaps.Get(mapFilter);

                    var certificateMapResponse = certificateMapRequest.Execute();
                    _logger.LogTrace(
                        $"certificateMapResponse Json {JsonConvert.SerializeObject(certificateMapResponse)}");

                    _logger.MethodExit();
                    return certificateMapResponse;
                }

                _logger.MethodExit();
                return null;
            }
            catch (Exception e)
            {
                _logger.LogError($"Error occured in Management.CreateMap: {LogHandler.FlattenException(e)}");
                throw;
            }
        }

        private CertificateMapEntry CreateMapEntry(CertificateMapEntry mapEntry, CertificateManagerService client,
            string parent)
        {
            try
            {
                _logger.MethodEntry();
                var certificateMapEntryListRequest =
                    client.Projects.Locations.CertificateMaps.CertificateMapEntries.List(parent);
                certificateMapEntryListRequest.Filter = $"name=\"{mapEntry.Name}\"";

                var certificateMapEntryListResponse = certificateMapEntryListRequest.Execute();
                _logger.LogTrace(
                    $"certificateMapEntryListResponse Json {JsonConvert.SerializeObject(certificateMapEntryListResponse)}");

                if (certificateMapEntryListResponse?.CertificateMapEntries?.Count > 0)
                {
                    _logger.MethodExit();
                    return certificateMapEntryListResponse.CertificateMapEntries[0];
                }

                var certificateMapEntryCreateRequest =
                    client.Projects.Locations.CertificateMaps.CertificateMapEntries.Create(mapEntry, parent);
                certificateMapEntryCreateRequest.CertificateMapEntryId = mapEntry.Name;

                var certificateMapEntryCreateResponse = certificateMapEntryCreateRequest.Execute();
                WaitForOperation(client, certificateMapEntryCreateResponse.Name);
                _logger.LogTrace(
                    $"certificateMapEntryCreateResponse Json {JsonConvert.SerializeObject(certificateMapEntryCreateResponse)}");

                if (certificateMapEntryCreateResponse?.Name?.Length > 0)
                {
                    var certificateMapEntryRequest =
                        client.Projects.Locations.CertificateMaps.CertificateMapEntries.Get(parent +
                            "/certificateMapEntries/" + mapEntry.Name);

                    var certificateMapEntryResponse = certificateMapEntryRequest.Execute();
                    _logger.LogTrace(
                        $"certificateMapEntryResponse Json {JsonConvert.SerializeObject(certificateMapEntryResponse)}");

                    _logger.MethodExit();
                    return certificateMapEntryResponse;
                }

                _logger.MethodExit();
                return null;
            }
            catch (Exception e)
            {
                _logger.LogError($"Error occured in Management.CreateMapEntry: {LogHandler.FlattenException(e)}");
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

        private string GetMapSettingsFromAlias(string alias, string nameType)
        {
            try
            {
                _logger.MethodEntry();
                //alias should be in format MapName/MapEntryName/CertificateName
                _logger.LogTrace($"nameType: {nameType}  alias: {alias}");
                var aliasComponents = alias.Split('/');
                if (aliasComponents.Length == 3 && nameType == "map") return aliasComponents[0].ToLower();
                if (aliasComponents.Length == 3 && nameType == "mapentry") return aliasComponents[1].ToLower();
                if (aliasComponents.Length == 3 && nameType == "certificate") return aliasComponents[2].ToLower();
                if (aliasComponents.Length == 1 && nameType == "certificate" && aliasComponents[0].Length > 0)
                    return aliasComponents[0].ToLower();

                _logger.MethodExit();
                return "";
            }
            catch (Exception e)
            {
                _logger.LogError(
                    $"Error in Management.GetMapSettingsFromAlias {LogHandler.FlattenException(e)}");
                throw;
            }
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