### PivotalServices.WcfClient.Kerberos.Interceptor

Stable versions are available at www.nuget.org https://www.nuget.org/packages/PivotalServices.WcfClient.Kerberos.Interceptor (please refer to the note at the end for more details)

This package will add a Wcf client interceptor which will injects kerberos ticket for egress requests. This should be used together with the supply buildpack https://github.com/cloudfoundry-community/kerberos-auth-egress-buildpack to make it fully functional

#### Below are the important developer instructions, to follow after installation of this package

1. Add the supply buildpack from "https://github.com/cloudfoundry-community/kerberos-auth-egress-buildpack/releases" in the CF manifest (preferably the latest release). 
   IMPORTANT: Make sure the application is built/published with target platform 'x64'
2. Follow the readme of https://github.com/cloudfoundry-community/kerberos-auth-egress-buildpack to setup the sources for kerberos config and keytab files
   For kerberos config template, please here https://github.com/cloudfoundry-community/kerberos-auth-egress-wcf-client-interceptor/blob/master/src/RouteServiceIwaWcfInterceptor/krb5.ini
3. Set the correct client UPN in AppSettings with key 'ClientUserPrincipalName' as below (this section will be already added by the package)
```xml
  <appSettings>
		<add key="ClientUserPrincipalName" value="client_username@domain" />
  </appSettings>
```
4. Set `ImpersonateClientUser` to `true` if you need to impersonate the svc user with the client user account (this section will be already added by the package, default is `false`)
```xml
  <appSettings>
		<add key="ImpersonateClientUser" value="false" />
  </appSettings>
```
5. Target service UPN has to be provided in the client/endpoint/identity configuration as in the sample below. If not, system will try to use the SPN 'host/foo.bar' (based on the below sample)
```xml
  <system.serviceModel>
		<client>
		  <endpoint address="http://foo.bar/myservice.svc" 
					binding="basicHttpBinding" 
					bindingConfiguration="BasicHttpBinding" 
					contract="MyService.IService" 
					name="BasicHttpBinding_IService"
					behaviorConfiguration ="myIwaInterceptorBehavior">
					<identity>
						<userPrincipalName value="target_user@domain" />
					</identity>
		  </endpoint>
		</client>
	</system.serviceModel>
  ```
6. To see debug logs, please set the log level to "Debug" or "Trace", via environment variable "PivotalIwaWcfClientInterceptor:LogLevel:Default" 

##### Notes
1. The dev/alpha packages are available at https://www.myget.org/feed/ajaganathan/package/nuget/Pivotal.WcfClient.Kerberos.Interceptor, feed https://www.myget.org/F/ajaganathan/api/v3/index.json
2. The packages are still in beta version as it still depends on a beta version of GssKerberos package.
