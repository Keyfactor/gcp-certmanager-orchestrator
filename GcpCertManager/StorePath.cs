using System.ComponentModel;
using Newtonsoft.Json;

namespace Keyfactor.Extensions.Orchestrator.GcpCertManager
{
    internal class StorePath
    {

        [JsonProperty("Locations")]
        [DefaultValue("global")]
        public string Locations { get; set; }
        [JsonProperty("Certificate Map Name")]
        public string CertificateMapName { get; set; }
        [JsonProperty("Certificate Map Entry Name")]
        public string CertificateMapEntryName { get; set; }
        [JsonProperty("Scope")]
        public string Scope { get; set; }
        

    }

}