using System.IO;
using Google.Apis.Auth.OAuth2;
using Google.Apis.CertificateManager.v1;
using Google.Apis.Services;

namespace Keyfactor.Extensions.Orchestrator.GcpCertManager.Client
{
    public class GcpCertificateManagerClient
    {
        public CertificateManagerService GetGoogleCredentials(string credentialFileName)
        {
            //Credentials file needs to be in the same location of the executing assembly
            var strExeFilePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var strWorkPath = Path.GetDirectoryName(strExeFilePath);
            var strSettingsJsonFilePath = Path.Combine(strWorkPath ?? string.Empty, credentialFileName);

            var stream = new FileStream(strSettingsJsonFilePath,
                FileMode.Open
            );

            var credentials = GoogleCredential.FromStream(stream);

            var service = new CertificateManagerService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = credentials
            });

            return service;
        }

        public enum CreateMapStatuses
        {
            MapAlreadyExists=0,
            MapCreated=1,
            ErrorCreatingMap=2
        }

    }
}
