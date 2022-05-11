using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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

        private static readonly Func<string, string> Pemify = ss =>
            ss.Length <= 64 ? ss : ss.Substring(0, 64) + "\n" + Pemify(ss.Substring(64));

        private readonly ILogger<Management> _logger;

        public Management(ILogger<Management> logger)
        {
            _logger = logger;
        }

        protected internal virtual AsymmetricKeyEntry KeyEntry { get; set; }

        public string ExtensionName => "GcpCertManager";

        protected internal string MapName { get; set; }

        protected internal string MapEntryName { get; set; }

        protected internal string CertificateName { get; set; }


        public JobResult ProcessJob(ManagementJobConfiguration jobConfiguration)
        {
            MapName = GetMapsettingsFromAlias(jobConfiguration.JobCertificate.Alias, "map");
            MapEntryName = GetMapsettingsFromAlias(jobConfiguration.JobCertificate.Alias, "mapentry");
            CertificateName = GetMapsettingsFromAlias(jobConfiguration.JobCertificate.Alias, "certificate");
            return PerformManagement(jobConfiguration);
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

                return complete;
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

                StorePath storeProps = JsonConvert.DeserializeObject<StorePath>(config.CertificateStoreDetails.Properties, new JsonSerializerSettings { DefaultValueHandling = DefaultValueHandling.Populate });
                var location = storeProps.Location;
                var storePath = $"projects/{config.CertificateStoreDetails.StorePath}/locations/{location}";
                var client = new GcpCertificateManagerClient();
                var svc = client.GetGoogleCredentials(config.CertificateStoreDetails.ClientMachine);

                DeleteCertificate(config, CertificateName, svc, storePath);

                return new JobResult
                {
                    Result = OrchestratorJobStatusJobResult.Success,
                    JobHistoryId = config.JobHistoryId,
                    FailureMessage = ""
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
                
                StorePath storeProps = JsonConvert.DeserializeObject<StorePath>(config.CertificateStoreDetails.Properties, new JsonSerializerSettings { DefaultValueHandling = DefaultValueHandling.Populate });
                var location = storeProps.Location;
                var storePath = $"projects/{config.CertificateStoreDetails.StorePath}/locations/{location}";
                var client = new GcpCertificateManagerClient();
                var svc = client.GetGoogleCredentials(config.CertificateStoreDetails.ClientMachine);

                _logger.LogTrace(
                    "Google Certificate Manager Client Created");

                var duplicate = CheckForDuplicate(storePath, CertificateName, svc);
                _logger.LogTrace($"Duplicate? = {duplicate}");

                //Check for Duplicate already in Google Certificate Manager, if there, make sure the Overwrite flag is checked before replacing
                if (duplicate && config.Overwrite || !duplicate)
                {
                    _logger.LogTrace("Either not a duplicate or overwrite was chosen....");
                    if (!string.IsNullOrWhiteSpace(config.JobCertificate.PrivateKeyPassword)) // This is a PFX Entry
                    {
                        _logger.LogTrace($"Found Private Key {config.JobCertificate.PrivateKeyPassword}");

                        if (string.IsNullOrWhiteSpace(config.JobCertificate.Alias)) _logger.LogTrace("No Alias Found");

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

                        //2. Create the certificate in Google
                        var gCertificate = new Certificate
                        {
                            SelfManaged = new SelfManagedCertificate
                                {PemCertificate = pubCertPem, PemPrivateKey = privateKeyString},
                            Name = CertificateName,
                            Description = CertificateName,
                            Scope = "DEFAULT" //Scope does not come back in inventory so just hard code it for now
                        };

                        X509Certificate replaceCertificateResponse;
                        if (duplicate && config.Overwrite)
                            replaceCertificateResponse = ReplaceCertificate(config, gCertificate, svc, storePath, true);
                        else
                            replaceCertificateResponse =
                                ReplaceCertificate(config, gCertificate, svc, storePath, false);

                        _logger.LogTrace($"Certificate Created with SubjectDn {replaceCertificateResponse.SubjectDN}");

                        if (MapName.Length > 0 && MapEntryName.Length > 0)
                        {
                            //Get the host name to be passed into the create map call
                            var subject = GetCommonNameFromSubject(replaceCertificateResponse.SubjectDN.ToString());

                            var createCertificateMapEntryBody = new CertificateMapEntry
                            {
                                Name = MapEntryName,
                                Description = MapEntryName,
                                Hostname = subject,
                                Certificates = new List<string> { $"{storePath}/certificates/{gCertificate.Name}" }
                            };

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

                return new JobResult
                {
                    Result = OrchestratorJobStatusJobResult.Failure,
                    JobHistoryId = config.JobHistoryId,
                    FailureMessage =
                        $"Duplicate alias {config.JobCertificate.Alias} found in Google Certificate Manager, to overwrite use the overwrite flag."
                };
            }
            catch (Google.GoogleApiException e)
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

        private X509Certificate ReplaceCertificate(ManagementJobConfiguration config, Certificate gCertificate,
            CertificateManagerService svc, string storePath, bool overwrite)
        {
            try
            {
                //DEFAULT or EDGE_CACHE
                //todo add labels 
                //Path does not support cert and private key replacement so delete and insert instead
                if (overwrite) DeleteCertificate(config, gCertificate.Name, svc, storePath);

                var replaceCertificateRequest = svc.Projects.Locations.Certificates.Create(gCertificate, storePath);
                replaceCertificateRequest.CertificateId = gCertificate.Name;
                var replaceCertificateResponse = replaceCertificateRequest.Execute();


                _logger.LogTrace(
                    $"Certificate Created in Google Cert Manager with Name {replaceCertificateResponse.Name}");

                var pemString = gCertificate.SelfManaged.PemCertificate;
                pemString = pemString.Replace("-----BEGIN CERTIFICATE-----", "")
                    .Replace("-----END CERTIFICATE-----", "");
                var buffer = Convert.FromBase64String(pemString);
                var parser = new X509CertificateParser();
                var cert = parser.ReadCertificate(buffer);

                return cert;
            }
            catch (Exception e)
            {
                _logger.LogError($"Error occured in Management.CreateCertificate: {LogHandler.FlattenException(e)}");
                throw;
            }
        }

        private void DeleteCertificate(ManagementJobConfiguration config, string certificateName,
            CertificateManagerService svc, string storePath)
        {
            try
            {

                //See if map entry exists, if so delete it
                var certificateMapEntryListRequest =
                    svc.Projects.Locations.CertificateMaps.CertificateMapEntries.List(storePath + $"/certificateMaps/{MapName}");
                certificateMapEntryListRequest.Filter = $"name=\"{storePath}/certificateMaps/{MapName}/certificateMapEntries/{MapEntryName}\"";
                var certificateMapEntryListResponse = certificateMapEntryListRequest.Execute();
                
                if (certificateMapEntryListResponse?.CertificateMapEntries?.Count > 0)
                {
                    var deleteCertificateMapEntryRequest =
                        svc.Projects.Locations.CertificateMaps.CertificateMapEntries.Delete(storePath +
                            $"/certificateMaps/{MapName}/certificateMapEntries/{MapEntryName}");
                    var deleteCertificateMapEntryResponse = deleteCertificateMapEntryRequest.Execute();
                    _logger.LogTrace(
                        $"Deleted {deleteCertificateMapEntryResponse.Name} Certificate Map Entry During Replace Procedure");
                }

                var certificatesRequest = svc.Projects.Locations.Certificates.List(storePath);
                certificatesRequest.Filter = $"name=\"{storePath}/certificates/{certificateName}\"";
                var certificatesResponse = certificatesRequest.Execute();
                if (certificatesResponse?.Certificates?.Count > 0)
                {

                    var deleteCertificateRequest =
                        svc.Projects.Locations.Certificates.Delete(storePath + $"/certificates/{certificateName}");
                    var deleteCertificateResponse = deleteCertificateRequest.Execute();
                    _logger.LogTrace($"Deleted {deleteCertificateResponse.Name} Certificate During Replace Procedure");
                }

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
                var certificateMapListRequest = client.Projects.Locations.CertificateMaps.List(parent);
                var mapFilter = $"{parent}/certificateMaps/{mapName}";
                certificateMapListRequest.Filter = $"name=\"{mapFilter}\"";

                var certificateMapListResponse = certificateMapListRequest.Execute();
                if (certificateMapListResponse?.CertificateMaps?.Count > 0)
                    return certificateMapListResponse.CertificateMaps[0];

                var certificateMapBody = new CertificateMap {Name = mapName, Description = mapName};
                var certificateMapCreateRequest =
                    client.Projects.Locations.CertificateMaps.Create(certificateMapBody, parent);
                certificateMapCreateRequest.CertificateMapId = mapName;
                var certificateMapCreateResponse = certificateMapCreateRequest.Execute();
                if (certificateMapCreateResponse?.Name?.Length > 0)
                {
                    var certificateMapRequest =
                        client.Projects.Locations.CertificateMaps.Get(mapFilter);
                    var certificateMapResponse = certificateMapRequest.Execute();
                    return certificateMapResponse;
                }

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
                var certificateMapEntryListRequest =
                    client.Projects.Locations.CertificateMaps.CertificateMapEntries.List(parent);
                certificateMapEntryListRequest.Filter = $"name=\"{mapEntry.Name}\"";

                var certificateMapEntryListResponse = certificateMapEntryListRequest.Execute();
                if (certificateMapEntryListResponse?.CertificateMapEntries?.Count > 0)
                    return certificateMapEntryListResponse.CertificateMapEntries[0];

                var certificateMapEntryCreateRequest =
                    client.Projects.Locations.CertificateMaps.CertificateMapEntries.Create(mapEntry, parent);
                certificateMapEntryCreateRequest.CertificateMapEntryId = mapEntry.Name;

                var certificateMapEntryCreateResponse = certificateMapEntryCreateRequest.Execute();
                if (certificateMapEntryCreateResponse?.Name?.Length > 0)
                {
                    var certificateMapEntryRequest =
                        client.Projects.Locations.CertificateMaps.CertificateMapEntries.Get(parent +
                            "/certificateMapEntries/" + mapEntry.Name);
                    var certificateMapEntryResponse = certificateMapEntryRequest.Execute();
                    return certificateMapEntryResponse;
                }

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
                var certificatesRequest =
                    client.Projects.Locations.Certificates.List(path);
                certificatesRequest.Filter = $"name=\"{path}/certificates/{alias}\"";

                var certificatesResponse = certificatesRequest.Execute();

                if (certificatesResponse?.Certificates?.Count == 1) return true;

                return false;
            }
            catch (Exception e)
            {
                _logger.LogError(
                    $"Error Checking for Duplicate Cert in Management.CheckForDuplicate {LogHandler.FlattenException(e)}");
                throw;
            }
        }

        private string GetMapsettingsFromAlias(string alias,string nameType)
        {
            //alias should be in format MapName/MapEntryName/CertificateName
            var aliasComponents = alias.Split('/');
            if (aliasComponents.Length == 3 && nameType=="map")
            {
                return aliasComponents[0].ToLower();
            }
            if (aliasComponents.Length == 3 && nameType == "mapentry")
            {
                return aliasComponents[1].ToLower();
            }
            if (aliasComponents.Length == 3 && nameType == "certificate")
            {
                return aliasComponents[2].ToLower();
            }
            if (aliasComponents.Length == 1 && nameType == "certificate" && aliasComponents[0].Length>0)
            {
                return aliasComponents[0].ToLower();
            }

            return "";
        }

        private string GetCommonNameFromSubject(string subject)
        {
            try
            {
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