using Microsoft.AspNetCore.Authentication.GssKerberos;
using Microsoft.Extensions.Logging;
using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Security;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.ServiceModel.Dispatcher;
using System.Text.RegularExpressions;

namespace Pivotal.RouteServiceIwaWcfInterceptor
{
    public class SvcRequestIwaInterceptor : IClientMessageInspector
    {
        const string APP_BIN_PATH_ENV_NM = "APP_BIN_PATH";
        const string CONTAINER_APP_DEFAULT_BIN_PATH = @"C:\Users\vcap\app\bin";
        const string KBBS_CONFIG_FILE_LOCATION_ENV_NM = "KRB5_CONFIG";
        readonly string APP_BIN_PATH;
        private const string AUTHORIZATION_HEADER = "Authorization";
        string kbbsConfigLocation;

        public SvcRequestIwaInterceptor()
        {
            kbbsConfigLocation = Environment.GetEnvironmentVariable(KBBS_CONFIG_FILE_LOCATION_ENV_NM);

            APP_BIN_PATH = Environment.GetEnvironmentVariable(APP_BIN_PATH_ENV_NM) ?? CONTAINER_APP_DEFAULT_BIN_PATH;
            this.Logger().LogDebug($"Using app bin path '{APP_BIN_PATH}', you can override this by setting environment variable '{APP_BIN_PATH_ENV_NM}'");
        }

        public void AfterReceiveReply(ref Message reply, object correlationState)
        {

        }

        public object BeforeSendRequest(ref Message request, IClientChannel channel)
        {
            try
            {
                if (channel.RemoteAddress.Identity == null)
                {
                    this.Logger().LogWarning($"Skipping kerberosticket injection for endpoint {channel.RemoteAddress.Uri} because identity (upn) is not set!");
                    return string.Empty;
                }

                if (string.IsNullOrWhiteSpace(kbbsConfigLocation))
                {
                    kbbsConfigLocation = Path.Combine(APP_BIN_PATH, "krb5.ini");
                }

                this.Logger().LogDebug($"Using kbbs config file location '{kbbsConfigLocation}'");

                if (!File.Exists(kbbsConfigLocation))
                {
                    var errMessage = $"File '{kbbsConfigLocation}' does not exist!";
                    throw new FileNotFoundException(errMessage);
                }

                var clientUpn = ConfigurationManager.AppSettings["ClientUserPrincipalName"];

                if (string.IsNullOrWhiteSpace(clientUpn))
                    throw new Exception($"ClientUserPrincipalName is not set in AppSettings");

                this.Logger().LogDebug($"Using client UPN '{clientUpn}'");

                var targetServiceUpn = (string)channel.RemoteAddress.Identity.IdentityClaim.Resource;

                this.Logger().LogDebug($"Using Target UPN '{targetServiceUpn}'");

                var ticket = GetKerberosTicket(targetServiceUpn, clientUpn);

                HttpRequestMessageProperty httpRequestMessage;
                object httpRequestMessageObject;
                if (request.Properties.TryGetValue(HttpRequestMessageProperty.Name, out httpRequestMessageObject))
                {
                    httpRequestMessage = httpRequestMessageObject as HttpRequestMessageProperty;
                    if (string.IsNullOrEmpty(httpRequestMessage.Headers[AUTHORIZATION_HEADER]))
                    {
                        httpRequestMessage.Headers[AUTHORIZATION_HEADER] = ticket;
                    }
                }
                else
                {
                    httpRequestMessage = new HttpRequestMessageProperty();
                    httpRequestMessage.Headers.Add(AUTHORIZATION_HEADER, ticket);
                    request.Properties.Add(HttpRequestMessageProperty.Name, httpRequestMessage);
                }
            }
            catch (Exception exception)
            {
                this.Logger().LogError($"WcfInterceptor error occurred, with exception {exception}");
            }

            return string.Empty;
        }

        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        private string GetKerberosTicket(string targetServiceUpn, string clientUpn)
        {
            this.Logger().LogDebug($"Getting TGT for UPN '{clientUpn}'");
            EnsureTgt(clientUpn);

            this.Logger().LogDebug($"Getting client credentials for UPN '{clientUpn}' using the provided keytab file");
            using (var clientCredentials = GssCredentials.FromKeytab(clientUpn, CredentialUsage.Initiate))
            {
                this.Logger().LogDebug($"Initiating kerberos client connection");
                using (var initiator = new GssInitiator(credential: clientCredentials, spn: targetServiceUpn))
                {
                    try
                    {
                        this.Logger().LogDebug($"Getting kerberos ticket for UPN '{clientUpn}'");
                        var kerberosTicket = Convert.ToBase64String(initiator.Initiate(null));
                        this.Logger().LogTrace($"Ticket: {kerberosTicket}");
                        return $"Negotiate {kerberosTicket}";
                    }
                    catch (GssException exception)
                    {
                        this.Logger().LogError(exception.Message);
                        return string.Empty;
                    }
                }
            }
        }

        private void EnsureTgt(string principal)
        {
            var expiry = GetTgtExpiry();
            if (expiry < DateTime.Now)
                ObtainTgt(principal);
        }

        private DateTime GetTgtExpiry()
        {
            var executablePath = Path.Combine(APP_BIN_PATH, "klist.exe");

            if (!File.Exists(executablePath))
                throw new FileNotFoundException("Please ensure to add the latest version of cf supply buildpack from https://github.com/cloudfoundry-community/kerberos-auth-egress-buildpack/releases", executablePath);

            try
            {
                var klistResult = RunCmd(executablePath, null);
                var tgtExpiryMatch = Regex.Match(klistResult, ".{17}(?=  krbtgt)");
                if (tgtExpiryMatch.Success && DateTime.TryParse(tgtExpiryMatch.Value, out var expiry))
                    return expiry;
            }
            catch (Exception exception)
            {
                this.Logger().LogError(exception.Message);
            }

            return DateTime.MinValue;
        }
        private void ObtainTgt(string principal)
        {
            var executablePath = Path.Combine(APP_BIN_PATH, "kinit.exe");

            if (!File.Exists(executablePath))
                throw new FileNotFoundException("Please ensure to add the latest version of cf supply buildpack from https://github.com/cloudfoundry-community/kerberos-auth-egress-buildpack/releases", executablePath);

            RunCmd(executablePath, $"-k -i {principal}");
        }

        private string RunCmd(string cmd, string args)
        {
            var cmdsi = new ProcessStartInfo(cmd)
            {
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            this.Logger().LogDebug($"Executing command '{cmd}' with args '{args}'");

            var proc = Process.Start(cmdsi);
            var result = proc.StandardOutput.ReadToEnd();
            var err = proc.StandardError.ReadToEnd();
            if (!string.IsNullOrWhiteSpace(err)) throw new Exception(err);
            proc.WaitForExit();
            return result;
        }
    }
}