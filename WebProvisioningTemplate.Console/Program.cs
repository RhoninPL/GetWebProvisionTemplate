using System.Security;
using Microsoft.SharePoint.Client;
using OfficeDevPnP.Core.Framework.Provisioning.ObjectHandlers;
using OfficeDevPnP.Core.Framework.Provisioning.Providers.Xml;

namespace WebProvisioningTemplate.Console
{
    public class Program
    {
        internal static void Main()
        {
            var url = ConsoleReader.GetInput("Enter URL web to download provision template:");
            var user = ConsoleReader.GetInput("Enter username:");
            var password = ConsoleReader.GetPassword("Enter password:");
            var path = ConsoleReader.GetInput("Output directory:");

            var clientContext = new ClientContext(url) { Credentials = new SharePointOnlineCredentials(user, GetSecureString(password)) };
            GetProvisioningTemplate(clientContext, path);
        }

        private static void GetProvisioningTemplate(ClientContext clientContext, string path = @"c:\temp\pnpprovisioningdemo")
        {
            var webTemplateCreationInformation = new ProvisioningTemplateCreationInformation(clientContext.Web);
            var template = clientContext.Web.GetProvisioningTemplate(webTemplateCreationInformation);
            XMLTemplateProvider provider = new XMLFileSystemTemplateProvider(@"c:\temp\pnpprovisioningdemo", "");
            provider.SaveAs(template, "PnPProvisioning.xml");
        }

        private static SecureString GetSecureString(string password)
        {
            var pwd = new SecureString();
            foreach (char c in password)
                pwd.AppendChar(c);

            return pwd;
        }
    }
}
