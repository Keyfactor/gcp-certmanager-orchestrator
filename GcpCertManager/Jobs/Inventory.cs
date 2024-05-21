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

        public string ExtensionName => "";

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

                StoreProperties storeProperties = JsonConvert.DeserializeObject<StoreProperties>(config.CertificateStoreDetails.Properties,
                    new JsonSerializerSettings {DefaultValueHandling = DefaultValueHandling.Populate});
                storeProperties.ProjectId = config.CertificateStoreDetails.ClientMachine;

                _logger.LogTrace($"Store Properties:");
                _logger.LogTrace($"  Location: {storeProperties.Location}"); 
                _logger.LogTrace($"  Project Id: {storeProperties.ProjectId}");
                _logger.LogTrace($"  Service Account Key Path: {storeProperties.ServiceAccountKey}");

                _logger.LogTrace("Getting Credentials from Google...");
                var svc = new GcpCertificateManagerClient().GetGoogleCredentials(storeProperties.ServiceAccountKey);
                _logger.LogTrace("Got Credentials from Google");

                var warningFlag = false;
                var sb = new StringBuilder();
                sb.Append("");
                var inventoryItems = new List<CurrentInventoryItem>();
                var nextPageToken = string.Empty;

                //todo support labels
                var storePath = $"projects/{storeProperties.ProjectId}/locations/{storeProperties.Location}";

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
                    if (certificatesResponse?.Certificates != null)
                        inventoryItems.AddRange(certificatesResponse.Certificates.Select(
                            c =>
                            {
                                try
                                {
                                    _logger.LogTrace(
                                        $"Building Cert List Inventory Item Alias: {c.Name} Pem: {c.PemCertificate} Private Key: dummy (from PA API)");
                                    return BuildInventoryItem(c.Name, c.PemCertificate,
                                        true, storePath, svc);
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

                        nextPageToken = certificatesResponse.NextPageToken;
                } while (nextPageToken?.Length > 0);

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
                var googleError = e.Error?.ErrorResponseContent + " " + LogHandler.FlattenException(e);

                _logger.LogError($"PerformInventory Error: {LogHandler.FlattenException(e)}");
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
            string storePath, CertificateManagerService svc)
        {
            try
            {
                _logger.MethodEntry();
                _logger.LogTrace($"Alias: {alias} Pem: {certPem} PrivateKey: {privateKey}");

                //1. Look up certificate map entries based on certificate name
                var certAttributes = GetCertificateAttributes(storePath);
                var modAlias = alias.Split('/')[5];

                _logger.LogTrace($"Got modAlias: {modAlias}");

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
                _logger.LogError(
                    $"Error Occurred in Inventory.GetCertificateAttributes: {LogHandler.FlattenException(e)}");
                throw;
            }
        }
    }
}