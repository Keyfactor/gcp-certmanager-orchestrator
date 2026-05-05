// Copyright 2026 Keyfactor
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with the License.
// You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
// Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the specific language governing permissions
// and limitations under the License.

using System;
using System.Collections.Generic;
using System.Linq;
using Google.Apis.CloudResourceManager.v3;
using Google.Apis.CloudResourceManager.v3.Data;
using Keyfactor.Extensions.Orchestrator.GcpCertManager.Client;
using Keyfactor.Logging;
using Keyfactor.Orchestrators.Extensions;
using Keyfactor.Orchestrators.Extensions.Interfaces;
using Microsoft.Extensions.Logging;

namespace Keyfactor.Extensions.Orchestrator.GcpCertManager.Jobs
{
    public class Discovery : JobBase, IDiscoveryJobExtension
    {
        // GCP Certificate Manager exposes a "global" location plus a handful of regional
        // locations. Most certs live in "global" so default discovery to that. Operators
        // can override via the Discovery job's "Directories to search" field with a
        // comma-separated list (e.g. "global,us-central1,europe-west1").
        private static readonly HashSet<string> DefaultLocations =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "global" };

        // Keyfactor Command's Discovery form posts the comma-separated "Directories
        // to search" value into JobProperties. Try the common key names since the
        // exact casing has shifted across Command versions.
        private static readonly string[] DirsToSearchKeys = { "dirs", "Dirs", "directories", "Directories", "DirsToSearch" };

        // Optional override: path to a service account JSON file installed alongside
        // the orchestrator extension. When omitted, GoogleCredential.ApplicationDefault
        // is used - which is the recommended path when the orchestrator runs on a GCE
        // VM / GKE pod with a workload-identity-bound service account.
        private const string ServiceAccountKeyProperty = "ServiceAccountKey";

        public Discovery(IPAMSecretResolver resolver) : base(resolver)
        {
            Logger = LogHandler.GetClassLogger<Discovery>();
        }

        public string ExtensionName => "GcpCertMgr";

        public JobResult ProcessJob(DiscoveryJobConfiguration jobConfiguration,
            SubmitDiscoveryUpdate submitDiscoveryUpdate)
        {
            if (jobConfiguration == null)
            {
                Logger.LogError("ProcessJob called with null jobConfiguration.");
                return FailureResult(0, "DiscoveryJobConfiguration is null.");
            }

            if (submitDiscoveryUpdate == null)
            {
                Logger.LogError("ProcessJob called with null submitDiscoveryUpdate.");
                return FailureResult(jobConfiguration.JobHistoryId, "SubmitDiscoveryUpdate delegate is null.");
            }

            using (var flow = new FlowLogger(Logger, "GcpCertMgr-Discovery"))
            {
                try
                {
                    Logger.MethodEntry(LogLevel.Debug);
                    return PerformDiscovery(jobConfiguration, submitDiscoveryUpdate, flow);
                }
                catch (Exception e)
                {
                    var msg = DescribeException(e);
                    flow.Fail("ProcessJob", msg);
                    Logger.LogError(e, "Error In Discovery.ProcessJob: {ErrorMessage}", LogHandler.FlattenException(e));
                    return FailureResult(jobConfiguration.JobHistoryId,
                        $"Unknown exception in Discovery: {msg}", flow);
                }
                finally
                {
                    Logger.MethodExit(LogLevel.Debug);
                }
            }
        }

        private JobResult PerformDiscovery(DiscoveryJobConfiguration config,
            SubmitDiscoveryUpdate submitDiscovery, FlowLogger flow)
        {
            // ClientMachine is interpreted as the GCP Organization ID for logging /
            // labeling. The actual project set returned by Search() is controlled by the
            // service account's IAM bindings - the customer scopes that at the org root.
            var orgIdHint = (config.ClientMachine ?? string.Empty).Trim();
            flow.Step("ParseConfig", $"orgIdHint={(string.IsNullOrEmpty(orgIdHint) ? "<none>" : orgIdHint)}");

            string serviceAccountKey = null;
            flow.Step("ResolveServiceAccountKey", () =>
            {
                if (config.JobProperties != null &&
                    config.JobProperties.TryGetValue(ServiceAccountKeyProperty, out var raw))
                {
                    var s = raw?.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) serviceAccountKey = s.Trim();
                }
            }, $"source={(serviceAccountKey == null ? "ADC" : "JobProperties")}");

            var (locations, locationSource) = ResolveLocations(config);
            flow.Step("ResolveLocations",
                $"source={locationSource}, locations=[{string.Join(",", locations)}]");

            CloudResourceManagerService crm = null;
            flow.Step("CreateApiClient", () =>
            {
                crm = new GcpCertificateManagerClient().GetCloudResourceManager(serviceAccountKey);
            });

            List<Project> projects = null;
            flow.Step("ListProjects", () =>
            {
                projects = ListAccessibleProjects(crm, orgIdHint);
            }, "filter=state=ACTIVE");

            var activeProjects = projects ?? new List<Project>();
            flow.Step("ProjectsCount", $"count={activeProjects.Count}");

            var discoveredLocations = new List<string>();

            if (activeProjects.Count == 0)
            {
                flow.Skip("EmitStorePaths", "no accessible projects returned");
            }
            else
            {
                flow.Branch($"PerProject (projects={activeProjects.Count}, locations={locations.Count})");
                try
                {
                    foreach (var project in activeProjects)
                    {
                        var projectId = project?.ProjectId;
                        if (string.IsNullOrWhiteSpace(projectId))
                        {
                            flow.Skip("Project", "missing projectId");
                            continue;
                        }

                        foreach (var location in locations)
                        {
                            // Canonical GCP resource name. Operators approving the discovered
                            // store will need to set the store's ClientMachine to {projectId}
                            // and the Location custom property to {location} - documented in
                            // docsource/gcpcertmgr.md.
                            var storePath = $"projects/{projectId}/locations/{location}";
                            discoveredLocations.Add(storePath);
                            flow.Step($"Discovered-{storePath}");
                        }
                    }
                }
                finally
                {
                    flow.EndBranch();
                }
            }

            flow.Step("SubmitDiscovery", () => submitDiscovery.Invoke(discoveredLocations),
                $"locationCount={discoveredLocations.Count}");

            flow.Step("Result", $"SUCCESS - {discoveredLocations.Count} locations discovered");
            return SuccessResult(config.JobHistoryId, flow.GetSummary());
        }

        private static (HashSet<string> Locations, string Source) ResolveLocations(DiscoveryJobConfiguration config)
        {
            if (config?.JobProperties != null)
            {
                foreach (var key in DirsToSearchKeys)
                {
                    if (!config.JobProperties.TryGetValue(key, out var raw)) continue;
                    var s = raw?.ToString();
                    if (string.IsNullOrWhiteSpace(s)) continue;

                    var locations = new HashSet<string>(
                        s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(d => d.Trim().Trim('/'))
                            .Where(d => d.Length > 0),
                        StringComparer.OrdinalIgnoreCase);

                    if (locations.Count > 0)
                        return (locations, $"user (key={key})");
                }
            }

            return (DefaultLocations, "default");
        }

        private List<Project> ListAccessibleProjects(CloudResourceManagerService crm, string orgIdHint)
        {
            // Projects.Search() returns every active project the calling identity can
            // see across the organization, including those nested in folders. The
            // customer's service account is permissioned at the org root so this is the
            // correct boundary - tightening (or loosening) is done in IAM, not here.
            //
            // orgIdHint is logged but not used as a query filter: the v3 query syntax
            // `parent:organizations/{id}` only matches direct children, missing every
            // project that lives under a folder. Filtering by parent ancestry from the
            // client side requires N additional GetAncestry calls per project, which
            // doesn't scale and isn't necessary when IAM already constrains the result.
            var ids = new List<Project>();
            string nextPageToken = null;
            do
            {
                var req = crm.Projects.Search();
                req.Query = "state:ACTIVE";
                req.PageSize = 100;
                if (!string.IsNullOrEmpty(nextPageToken)) req.PageToken = nextPageToken;

                var resp = req.Execute();
                if (resp?.Projects != null)
                {
                    ids.AddRange(resp.Projects.Where(p =>
                        p != null &&
                        !string.IsNullOrWhiteSpace(p.ProjectId) &&
                        string.Equals(p.State, "ACTIVE", StringComparison.OrdinalIgnoreCase)));
                }
                nextPageToken = resp?.NextPageToken;
            } while (!string.IsNullOrEmpty(nextPageToken));

            if (!string.IsNullOrEmpty(orgIdHint))
            {
                Logger.LogTrace("Discovery returned {Count} accessible projects (orgIdHint={OrgIdHint}; not used as a query filter, see ListAccessibleProjects).",
                    ids.Count, orgIdHint);
            }

            return ids;
        }
    }
}
