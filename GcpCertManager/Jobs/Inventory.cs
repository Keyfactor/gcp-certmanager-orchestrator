using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;
using Google.Apis.CertificateManager.v1;
using Google.Apis.CertificateManager.v1.Data;
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
            _logger.MethodEntry(LogLevel.Debug);
            return PerformInventory(jobConfiguration, submitInventoryUpdate);
        }

        private JobResult PerformInventory(InventoryJobConfiguration config, SubmitInventoryUpdate submitInventory)
        {
            try
            {
                _logger.MethodEntry(LogLevel.Debug);
                _logger.LogTrace($"Inventory Config {JsonConvert.SerializeObject(config)}");
                _logger.LogTrace($"Client Machine: {config.CertificateStoreDetails.ClientMachine} ApiKey: {config.ServerPassword}");

                StorePath storeProps = JsonConvert.DeserializeObject<StorePath>(config.CertificateStoreDetails.Properties, new JsonSerializerSettings { DefaultValueHandling = DefaultValueHandling.Populate });

                var client = new GcpCertificateManagerClient();
                var svc=client.GetGoogleCredentials(config.CertificateStoreDetails.ClientMachine);
                _logger.LogTrace("Google Cert Manager Client Created");

                var warningFlag = false;
                var sb = new StringBuilder();
                sb.Append("");
                var inventoryItems = new List<CurrentInventoryItem>();
                var nextPageToken = string.Empty;

                //todo support labels and map entries by making api calls to search maps and map entries

                if (storeProps != null)
                    foreach (var location in storeProps.Locations.Split(','))
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
                            nextPageToken = null;
                            //Debug Write Certificate List Response from Google Cert Manager
                            _logger.LogTrace(
                                $"Certificate List Result {JsonConvert.SerializeObject(certificatesResponse)}");

                            inventoryItems.AddRange(certificatesResponse.Certificates.Select(
                                c =>
                                {
                                    try
                                    {
                                        _logger.LogTrace(
                                            $"Building Cert List Inventory Item Alias: {c.Name} Pem: {c.PemCertificate} Private Key: dummy (from PA API)");
                                        return BuildInventoryItem(c.Name, c.PemCertificate,
                                            true,storePath); //todo figure out how to see if private key exists not in Google Api return
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
                            {
                                nextPageToken = certificatesResponse.NextPageToken;
                            }
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
            catch (Exception e)
            {
                _logger.LogError($"PerformInventory Error: {e.Message}");
                throw;
            }

        }

        protected virtual CurrentInventoryItem BuildInventoryItem(string alias, string certPem, bool privateKey,string storePath)
        {
            try
            {
                _logger.MethodEntry();
                _logger.LogTrace($"Alias: {alias} Pem: {certPem} PrivateKey: {privateKey}");

                //1. Look up certificate map entries based on certificate name
                var certAttributes = GetCertificateAttributes(storePath + "/certificates/" + alias);

                var acsi = new CurrentInventoryItem
                {
                    Alias = alias,
                    Certificates = new[] {certPem},
                    ItemStatus = OrchestratorInventoryItemStatus.Unknown,
                    PrivateKeyEntry = privateKey,
                    UseChainLevel = false,
                    Parameters = certAttributes
                };

                return acsi;
            }
            catch (Exception e)
            {
                _logger.LogError($"Error Occurred in Inventory.BuildInventoryItem: {e.Message}");
                throw;
            }
        }

        protected new Dictionary<string, object> GetCertificateAttributes(string certificateName)
        {

            return new Dictionary<string, object>();
        }
    }
}