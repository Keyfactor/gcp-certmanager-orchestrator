using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Google;
using Google.Apis.CertificateManager.v1;
using Keyfactor.Extensions.Orchestrator.GcpCertManager.Client;
using Keyfactor.Logging;
using Keyfactor.Orchestrators.Common.Enums;
using Keyfactor.Orchestrators.Extensions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Keyfactor.Extensions.Orchestrator.GcpCertManager.Jobs
{
    public class Inventory : IInventoryJobExtension
    {
        private readonly ILogger<Inventory> _logger;

        public Inventory(ILogger<Inventory> logger)
        {
            _logger = logger;
        }

        public string ExtensionName => "GcpCertManager";

        public JobResult ProcessJob(InventoryJobConfiguration jobConfiguration,
            SubmitInventoryUpdate submitInventoryUpdate)
        {
            try
            {
                _logger.MethodEntry();
                return PerformInventory(jobConfiguration, submitInventoryUpdate);
            }
            catch (Exception e)
            {
                _logger.LogError($"Error occured in Inventory.ProcessJob: {LogHandler.FlattenException(e)}");
                throw;
            }
        }

        private JobResult PerformInventory(InventoryJobConfiguration config, SubmitInventoryUpdate submitInventory)
        {
            try
            {
                _logger.MethodEntry(LogLevel.Debug);
                _logger.LogTrace($"Inventory Config {JsonConvert.SerializeObject(config)}");
                _logger.LogTrace(
                    $"Client Machine: {config.CertificateStoreDetails.ClientMachine} ApiKey: {config.ServerPassword}");

                var storeProps = JsonConvert.DeserializeObject<StorePath>(config.CertificateStoreDetails.Properties,
                    new JsonSerializerSettings {DefaultValueHandling = DefaultValueHandling.Populate});

                _logger.LogTrace($"Store Properties: {JsonConvert.SerializeObject(storeProps)}");

                var client = new GcpCertificateManagerClient();
                _logger.LogTrace("Getting Credentials from Google...");
                var svc = client.GetGoogleCredentials(config.CertificateStoreDetails.ClientMachine);
                _logger.LogTrace($"Got Credentials from Google");


                var warningFlag = false;
                var sb = new StringBuilder();
                sb.Append("");
                var inventoryItems = new List<CurrentInventoryItem>();
                var nextPageToken = string.Empty;

                //todo support labels
                if (storeProps != null)
                    foreach (var location in storeProps.Location.Split(','))
                    {
                        var storePath = $"projects/{config.CertificateStoreDetails.StorePath}/locations/{location}";
                        do
                        {
                            var certificatesRequest =
                                svc.Projects.Locations.Certificates.List(storePath);
                            certificatesRequest.Filter = "pemCertificate!=\"\"";
                            certificatesRequest.PageSize = 100;
                            if (nextPageToken?.Length > 0) certificatesRequest.PageToken = nextPageToken;

                            var certificatesResponse = certificatesRequest.Execute();
                            _logger.LogTrace(
                                $"certificatesResponse: {JsonConvert.SerializeObject(certificatesResponse)}");

                            nextPageToken = null;
                            //Debug Write Certificate List Response from Google Cert Manager

                            inventoryItems.AddRange(certificatesResponse.Certificates.Select(
                                c =>
                                {
                                    try
                                    {
                                        _logger.LogTrace(
                                            $"Building Cert List Inventory Item Alias: {c.Name} Pem: {c.PemCertificate} Private Key: dummy (from PA API)");
                                        return BuildInventoryItem(c.Name, c.PemCertificate,
                                            true, storePath, svc,
                                            storeProps
                                                .ProjectNumber); //todo figure out how to see if private key exists not in Google Api return
                                    }
                                    catch
                                    {
                                        _logger.LogWarning(
                                            $"Could not fetch the certificate: {c?.Name} associated with description {c?.Description}.");
                                        sb.Append(
                                            $"Could not fetch the certificate: {c?.Name} associated with issuer {c?.Description}.{Environment.NewLine}");
                                        warningFlag = true;
                                        return new CurrentInventoryItem();
                                    }
                                }).Where(acsii => acsii?.Certificates != null).ToList());

                            if (certificatesResponse.NextPageToken?.Length > 0)
                                nextPageToken = certificatesResponse.NextPageToken;
                        } while (nextPageToken?.Length > 0);
                    }

                _logger.LogTrace("Submitting Inventory To Keyfactor via submitInventory.Invoke");
                submitInventory.Invoke(inventoryItems);
                _logger.LogTrace("Submitted Inventory To Keyfactor via submitInventory.Invoke");

                _logger.MethodExit(LogLevel.Debug);
                if (warningFlag)
                {
                    _logger.LogTrace("Found Warning");
                    return new JobResult
                    {
                        Result = OrchestratorJobStatusJobResult.Warning,
                        JobHistoryId = config.JobHistoryId,
                        FailureMessage = sb.ToString()
                    };
                }

                _logger.LogTrace("Return Success");
                return new JobResult
                {
                    Result = OrchestratorJobStatusJobResult.Success,
                    JobHistoryId = config.JobHistoryId,
                    FailureMessage = sb.ToString()
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
                _logger.LogError($"PerformInventory Error: {LogHandler.FlattenException(e)}");
                throw;
            }
        }

        protected virtual CurrentInventoryItem BuildInventoryItem(string alias, string certPem, bool privateKey,
            string storePath, CertificateManagerService svc, string projectNumber)
        {
            try
            {
                _logger.MethodEntry();
                _logger.LogTrace($"Alias: {alias} Pem: {certPem} PrivateKey: {privateKey}");

                //1. Look up certificate map entries based on certificate name
                var certAttributes = GetCertificateAttributes(storePath);
                var modAlias = alias.Split('/')[5];
                var mapSettings = GetMapSettings(storePath, modAlias, svc, projectNumber);

                _logger.LogTrace($"Got modAlias: {modAlias}, certAttributes and mapSettings");

                if (mapSettings != null && mapSettings.ContainsKey("Certificate Map Name") &&
                    mapSettings["Certificate Map Name"]?.Length > 0)
                    modAlias = mapSettings["Certificate Map Name"] + "/" + mapSettings["Certificate Map Entry Name"] +
                               "/" + modAlias;

                _logger.LogTrace($"Got modAlias after map additions: {modAlias}");

                var acsi = new CurrentInventoryItem
                {
                    Alias = modAlias,
                    Certificates = new[] {certPem},
                    ItemStatus = OrchestratorInventoryItemStatus.Unknown,
                    PrivateKeyEntry = privateKey,
                    UseChainLevel = false,
                    Parameters = certAttributes
                };

                _logger.MethodExit();
                return acsi;
            }
            catch (Exception e)
            {
                _logger.LogError($"Error Occurred in Inventory.BuildInventoryItem: {LogHandler.FlattenException(e)}");
                throw;
            }
        }

        protected Dictionary<string, object> GetCertificateAttributes(string storePath)
        {
            try
            {
                _logger.MethodEntry();
                _logger.LogTrace($"Store Path: {storePath}");
                var locationName = storePath.Split('/')[3];

                var siteSettingsDict = new Dictionary<string, object>
                {
                    {"Location", locationName}
                };

                _logger.MethodExit();
                return siteSettingsDict;
            }
            catch (Exception e)
            {
                _logger.LogError($"Error Occurred in Inventory.GetCertificateAttributes: {LogHandler.FlattenException(e)}");
                throw;
            }
        }


        protected Dictionary<string, string> GetMapSettings(string storePath, string certificateName,
            CertificateManagerService svc, string projectNumber)
        {
            try
            {
                _logger.MethodEntry();
                var locationName = storePath.Split('/')[3];
                var siteSettingsDict = new Dictionary<string, string>();
                var certName = $"projects/{projectNumber}/locations/{locationName}/certificates/{certificateName}";

                _logger.LogTrace($"certName: {certName}");

                //Loop through list of maps and map entries until you find the certificate
                var mapListRequest =
                    svc.Projects.Locations.CertificateMaps.List(storePath);

                var mapListResponse = mapListRequest.Execute();
                _logger.LogTrace(
                    $"mapListResponse: {JsonConvert.SerializeObject(mapListResponse)}");


                foreach (var map in mapListResponse.CertificateMaps)
                {
                    var mapEntryListRequest = svc.Projects.Locations.CertificateMaps.CertificateMapEntries.List(map.Name);
                    mapEntryListRequest.Filter = $"certificates:\"{certName}\"";
                    var mapEntryListResponse = mapEntryListRequest.Execute();
                    _logger.LogTrace(
                        $"mapEntryListResponse: {JsonConvert.SerializeObject(mapEntryListResponse)}");

                    if (mapEntryListResponse?.CertificateMapEntries?.Count > 0)
                    {
                        var mapEntry = mapEntryListResponse.CertificateMapEntries[0];
                        _logger.LogTrace($"mapEntry: {mapEntry}");
                        siteSettingsDict.Add("Certificate Map Name", map.Name.Split('/')[5]);
                        siteSettingsDict.Add("Certificate Map Entry Name", mapEntry.Name.Split('/')[7]);
                        _logger.MethodExit();
                        return siteSettingsDict;
                    }
                }
                _logger.MethodExit();
                return siteSettingsDict;
            }
            catch (Exception e)
            {
                _logger.LogError($"Error Occurred in Inventory.GetMapSettings: {LogHandler.FlattenException(e)}");
                throw;
            }
        }
    }
}