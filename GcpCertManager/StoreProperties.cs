// Copyright 2025 Keyfactor
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with the License.
// You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
// Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the specific language governing permissions
// and limitations under the License.
using System.ComponentModel;
using Newtonsoft.Json;

namespace Keyfactor.Extensions.Orchestrator.GcpCertManager
{
    internal class StoreProperties
    {
        [DefaultValue("global")]
        public string Location { get; set; }

        public string ProjectId { get; set; }

        public string ServiceAccountKey { get; set; }

        // GCP Certificate Manager's Scope field is create-only and immutable. Blank
        // means "let JobBase.ResolveScope pick DEFAULT" so existing stores upgrade
        // without operator intervention. Non-default scopes (ALL_REGIONS for
        // cross-region internal ALBs, EDGE_CACHE for Media CDN, CLIENT_AUTH for
        // mTLS trust configs) must be set per-store before the first Add.
        [DefaultValue("")]
        public string Scope { get; set; }
    }
}