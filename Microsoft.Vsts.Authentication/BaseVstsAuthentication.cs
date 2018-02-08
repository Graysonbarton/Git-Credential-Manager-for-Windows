﻿/**** Git Credential Manager for Windows ****
 *
 * Copyright (c) Microsoft Corporation
 * All rights reserved.
 *
 * MIT License
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the """"Software""""), to deal
 * in the Software without restriction, including without limitation the rights to
 * use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
 * the Software, and to permit persons to whom the Software is furnished to do so,
 * subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
 * FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
 * COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN
 * AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
 * WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE."
**/

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Clients.ActiveDirectory;

namespace Microsoft.Alm.Authentication
{
    /// <summary>
    /// Base functionality for performing authentication operations against Visual Studio Online.
    /// </summary>
    public abstract class BaseVstsAuthentication : BaseAuthentication
    {
        public const string DefaultResource = "499b84ac-1321-427f-aa17-267ca6975798";
        public const string DefaultClientId = "872cd9fa-d31f-45e0-9eab-6e460a02d1f1";
        public const string RedirectUrl = "urn:ietf:wg:oauth:2.0:oob";

        protected const string AdalRefreshPrefix = "ada";

        protected BaseVstsAuthentication(VstsTokenScope tokenScope, ICredentialStore personalAccessTokenStore)
        {
            if (tokenScope is null)
                throw new ArgumentNullException(nameof(tokenScope));
            if (personalAccessTokenStore is null)
                throw new ArgumentNullException(nameof(personalAccessTokenStore));

            ClientId = DefaultClientId;
            Resource = DefaultResource;
            TokenScope = tokenScope;
            PersonalAccessTokenStore = personalAccessTokenStore;
            VstsAuthority = new VstsAzureAuthority();
        }

        internal BaseVstsAuthentication(
            ICredentialStore personalAccessTokenStore,
            ITokenStore vstsIdeTokenCache,
            IVstsAuthority vstsAuthority)
            : this(VstsTokenScope.ProfileRead, personalAccessTokenStore)
        {
            if (vstsIdeTokenCache is null)
                throw new ArgumentNullException(nameof(vstsIdeTokenCache));
            if (vstsAuthority is null)
                throw new ArgumentNullException(nameof(vstsAuthority));

            VstsIdeTokenCache = vstsIdeTokenCache;
            VstsAuthority = vstsAuthority;
            VstsAdalTokenCache = TokenCache.DefaultShared;
        }

        /// <summary>
        /// The application client identity by which access will be requested.
        /// </summary>
        public readonly string ClientId;

        /// <summary>
        /// The Azure resource for which access will be requested.
        /// </summary>
        public readonly string Resource;

        /// <summary>
        /// The desired scope of the authentication token to be requested.
        /// </summary>
        public readonly VstsTokenScope TokenScope;

        internal TokenCache VstsAdalTokenCache { get; private set; }
        internal ITokenStore VstsIdeTokenCache{ get; private set; }

        internal ICredentialStore PersonalAccessTokenStore { get; set; }
        internal IVstsAuthority VstsAuthority { get; set; }
        internal Guid TenantId { get; set; }

        /// <summary>
        /// Deletes a `<see cref="Credential"/>` from the storage used by the authentication object.
        /// </summary>
        /// <param name="targetUri">The uniform resource indicator used to uniquely identify the credentials.</param>
        public override void DeleteCredentials(TargetUri targetUri)
        {
            BaseSecureStore.ValidateTargetUri(targetUri);

            Credential credentials = PersonalAccessTokenStore.ReadCredentials(targetUri);

            // This ought to be async, but the base type's method isn't.
            Task.Run(async () =>
            {
                // Attempt to validate the credentials, if they're truly invalid delete them.
                if (!await ValidateCredentials(targetUri, credentials))
                {
                    PersonalAccessTokenStore.DeleteCredentials(targetUri);

                    // Remove any related entries from the tenant cache because tenant change
                    // could the be source of the invalidation, and not purging the cache will
                    // trap the user in a limbo state of invalid credentials.
                    
                    // Deserialize the cache and remove any matching entry.
                    string tenantUrl = targetUri.ToString();
                    var cache = await DeserializeTenantCache();

                    // Attempt to remove the URL entry, if successful serialize the cache.
                    if (cache.Remove(tenantUrl))
                    {
                        await SerializeTenantCache(cache);
                    }
                }
            }).Wait();
        }

        /// <summary>
        /// Detects the backing authority of the end-point.
        /// <para/>
        /// Returns `<see langword="true"/>` if the authority is Visual Studio Online, along with the tenant identity; `<see langword="false"/>` otherwise.
        /// </summary>
        /// <param name="targetUri">The resource which the authority protects.</param>
        /// <param name="tenantId">The identity of the authority tenant; `<see cref="Guid.Empty"/>` otherwise.</param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1062:Validate arguments of public methods", MessageId = "0")]
        public static async Task<KeyValuePair<bool, Guid>> DetectAuthority(TargetUri targetUri)
        {
            const string VstsBaseUrlHost = "visualstudio.com";
            const string VstsResourceTenantHeader = "X-VSS-ResourceTenant";

            BaseSecureStore.ValidateTargetUri(targetUri);

            var tenantId = Guid.Empty;

            if (targetUri.Host.EndsWith(VstsBaseUrlHost, StringComparison.OrdinalIgnoreCase))
            {
                Git.Trace.WriteLine($"'{targetUri}' is subdomain of '{VstsBaseUrlHost}', checking AAD vs MSA.");

                string tenant = null;
                WebResponse response;

                if (StringComparer.OrdinalIgnoreCase.Equals(targetUri.Scheme, "http")
                    || StringComparer.OrdinalIgnoreCase.Equals(targetUri.Scheme, "https"))
                {
                    // Query the cache first
                    string tenantUrl = targetUri.ToString();

                    // Read the cache from disk
                    var cache = await DeserializeTenantCache();

                    // Check the cache for an existing value
                    if (cache.TryGetValue(tenantUrl, out tenantId))
                        return new KeyValuePair<bool, Guid>(true, tenantId);

                    try
                    {
                        // build a request that we expect to fail, do not allow redirect to sign in url
                        var request = WebRequest.CreateHttp(targetUri);
                        request.UserAgent = Global.UserAgent;
                        request.Method = "HEAD";
                        request.AllowAutoRedirect = false;
                        // get the response from the server
                        response = await request.GetResponseAsync();
                    }
                    catch (WebException exception)
                    {
                        response = exception.Response;
                    }

                    // if the response exists and we have headers, parse them
                    if (response != null && response.SupportsHeaders)
                    {
                        // find the VSTS resource tenant entry
                        tenant = response.Headers[VstsResourceTenantHeader];

                        if (!string.IsNullOrWhiteSpace(tenant)
                            && Guid.TryParse(tenant, out tenantId))
                        {
                            // Update the cache.
                            cache[tenantUrl] = tenantId;

                            // Write the cache to disk.
                            await SerializeTenantCache(cache);

                            // Success, notify the caller
                            return new KeyValuePair<bool, Guid>(true, tenantId);
                        }
                    }
                }
                else
                {
                    Git.Trace.WriteLine($"detected non-https based protocol: {targetUri.Scheme}.");
                }
            }

            // if all else fails, fallback to basic authentication
            return new KeyValuePair<bool, Guid>(false, tenantId);
        }

        /// <summary>
        /// Creates a new authentication broker based for the specified resource.
        /// <para/>
        /// Returns `<see langword="true"/>` if an authority could be determined; otherwise `<see langword="false"/>`.
        /// </summary>
        /// <param name="targetUri">The resource for which authentication is being requested.</param>
        /// <param name="scope">The scope of the access being requested.</param>
        /// <param name="personalAccessTokenStore">Storage container for personal access token secrets.</param>
        public static async Task<BaseAuthentication> GetAuthentication(
            TargetUri targetUri,
            VstsTokenScope scope,
            ICredentialStore personalAccessTokenStore)
        {
            BaseSecureStore.ValidateTargetUri(targetUri);
            if (scope is null)
                throw new ArgumentNullException(nameof(scope));
            if (personalAccessTokenStore is null)
                throw new ArgumentNullException(nameof(personalAccessTokenStore));

            BaseAuthentication authentication = null;

            var result = await DetectAuthority(targetUri);

            if (!result.Key)
                return null;

            // Query for the tenant's identity
            Guid tenantId = result.Value;

            // empty identity is MSA, anything else is AAD
            if (tenantId == Guid.Empty)
            {
                Git.Trace.WriteLine("MSA authority detected.");
                authentication = new VstsMsaAuthentication(scope, personalAccessTokenStore);
            }
            else
            {
                Git.Trace.WriteLine($"AAD authority for tenant '{tenantId}' detected.");
                authentication = new VstsAadAuthentication(tenantId, scope, personalAccessTokenStore);
                (authentication as VstsAadAuthentication).TenantId = tenantId;
            }

            return authentication;
        }

        /// <summary>
        /// Gets a <see cref="Credential"/> from the storage used by the authentication object.
        /// <para/>
        /// Returns a `<see cref="Credential"/>` if successful; otherwise `<see langword="null"/>`.
        /// </summary>
        /// <param name="targetUri">The uniform resource indicator used to uniquely identify the credentials.</param>
        public override Credential GetCredentials(TargetUri targetUri)
        {
            BaseSecureStore.ValidateTargetUri(targetUri);

            Credential credentials = null;

            try
            {
                if ((credentials = PersonalAccessTokenStore.ReadCredentials(targetUri)) != null)
                {
                    Git.Trace.WriteLine($"credentials for '{targetUri}' found.");
                }
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(exception);

                Git.Trace.WriteLine($"failed to read credentials from the secure store: {exception.GetType().Name}.");
            }

            return credentials;
        }

        /// <summary>
        /// Validates that a set of credentials grants access to the target resource.
        /// <para/>
        /// Returns `<see langword="true"/>` if successful; otherwise `<see langword="false"/>`.
        /// </summary>
        /// <param name="targetUri">The target resource to validate against.</param>
        /// <param name="credentials">The credentials to validate.</param>
        public async Task<bool> ValidateCredentials(TargetUri targetUri, Credential credentials)
        {
            return await VstsAuthority.ValidateCredentials(targetUri, credentials);
        }

        /// <summary>
        /// Generates a "personal access token" or service specific, usage restricted access token.
        /// <para/>
        /// Returns a "personal access token" for the user if successful; otherwise `<see langword="null"/>`.
        /// </summary>
        /// <param name="targetUri">The target resource for which to acquire the personal access token for.</param>
        /// <param name="accessToken">Azure Directory access token with privileges to grant access to the target resource.</param>
        /// <param name="options">Set of options related to generation of personal access tokens.</param>
        protected async Task<Credential> GeneratePersonalAccessToken(
            TargetUri targetUri,
            Token accessToken,
            PersonalAccessTokenOptions options)
        {
            BaseSecureStore.ValidateTargetUri(targetUri);

            if (accessToken is null)
                throw new ArgumentNullException(nameof(accessToken));

            VstsTokenScope requestedScope = TokenScope;

            if (options.TokenScope != null)
            {
                // Take the intersection of the authority scope and the requested scope
                requestedScope &= options.TokenScope;

                // If the result of the intersection is none, then fail
                if (string.IsNullOrWhiteSpace(requestedScope.Value))
                    throw new InvalidOperationException("Invalid scope requested. Requested scope would result in no access privileges.");
            }

            Credential credential = null;

            Token personalAccessToken;
            if ((personalAccessToken = await VstsAuthority.GeneratePersonalAccessToken(targetUri, accessToken, requestedScope, options.RequireCompactToken, options.TokenDuration)) != null)
            {
                credential = (Credential)personalAccessToken;

                Git.Trace.WriteLine($"personal access token created for '{targetUri}'.");

                try
                {
                    PersonalAccessTokenStore.WriteCredentials(targetUri, credential);
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine(exception);

                    Git.Trace.WriteLine($"failed to write credentials to the secure store: {exception.GetType().Name}.");
                }
            }

            return credential;
        }

        /// <summary>
        /// Generates a "personal access token" or service specific, usage restricted access token.
        /// <para/>
        /// Returns `<see langword="true"/>` if successful; `<see langword="false"/>` otherwise.
        /// </summary>
        /// <param name="targetUri">The target resource for which to acquire the personal access token for.</param>
        /// <param name="accessToken">Azure Directory access token with privileges to grant access to the target resource.</param>
        /// <param name="requestCompactToken">Generates a compact token if `<see langword="true"/>`; generates a self describing token if `<see langword="false"/>`.</param>
        protected async Task<Credential> GeneratePersonalAccessToken(
            TargetUri targetUri,
            Token accessToken,
            bool requestCompactToken)
        {
            BaseSecureStore.ValidateTargetUri(targetUri);

            if (accessToken is null)
                throw new ArgumentNullException(nameof(accessToken));

            Credential credential = null;

            Token personalAccessToken;
            if ((personalAccessToken = await VstsAuthority.GeneratePersonalAccessToken(targetUri, accessToken, TokenScope, requestCompactToken)) != null)
            {
                credential = (Credential)personalAccessToken;

                Git.Trace.WriteLine($"personal access token created for '{targetUri}'.");

                try
                {
                    PersonalAccessTokenStore.WriteCredentials(targetUri, credential);
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine(exception);

                    Git.Trace.WriteLine($"failed to write credentials to the secure store: {exception.GetType().Name}.");
                }
            }

            return credential;
        }

        private const char CachePairSeperator = '=';
        private const char CachePairTerminator = '\0';
        private const string CachePathDirectory = "GitCredentialManager";
        private const string CachePathFileName = "tenant.cache";

        private static async Task<Dictionary<string, Guid>> DeserializeTenantCache()
        {
            var encoding = new UTF8Encoding(false);
            var path = GetCachePath();

            string data = null;
            Dictionary<string, Guid> cache = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            Exception exception = null;

            // Attempt up to five times to read from the cache
            for (int i = 0; i < 5; i += 1)
            {
                try
                {
                    // Just open the file from disk, the tenant identities are not secret and
                    // therefore safely left as unencrypted plain text.
                    using (var stream = File.Open(path, FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read))
                    using (var inflate = new GZipStream(stream, CompressionMode.Decompress))
                    using (var reader = new StreamReader(inflate, encoding))
                    {
                        data = await reader.ReadToEndAsync();
                    }
                }
                catch when (i < 5)
                {
                    // Sleep the thread, and wait before trying again using progressive back off
                    System.Threading.Thread.Sleep(i + 1 * 100);
                }
                catch (Exception e)
                {
                    exception = e;
                }
            }

            if (exception != null)
            {
                System.Diagnostics.Debug.WriteLine(exception);

                Git.Trace.WriteLine($"failed to deserialize tenant cache: {exception.GetType().Name}.");

                return cache;
            }

            // Parse the inflated data
            if (data.Length > 0)
            {
                int last = 0;
                int next = -1;

                while ((next = data.IndexOf(CachePairTerminator, last)) > 0)
                {
                    int idx = data.IndexOf(CachePairSeperator, last, next - last);
                    if (idx > 0)
                    {
                        string key = data.Substring(last, idx - last);
                        string val = data.Substring(idx + 1, next - idx - 1);

                        Guid id;
                        if (Guid.TryParse(val, out id))
                        {
                            cache[key] = id;
                        }

                        last = next + 1;
                    }
                }
            }

            return cache;
        }

        private static string GetCachePath()
        {
            string path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            path = Path.Combine(path, CachePathDirectory);

            // Create the directory if necessary
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            // Append the file name to the path
            path = Path.Combine(path, CachePathFileName);

            return path;
        }

        private static async Task SerializeTenantCache(Dictionary<string, Guid> cache)
        {
            if (cache is null)
                throw new ArgumentNullException(nameof(cache));

            var encoding = new UTF8Encoding(false);
            string path = GetCachePath();

            StringBuilder builder = new StringBuilder();
            Exception exception = null;

            // Write each key/value pair as key=value\0
            foreach (var pair in cache)
            {
                builder.Append(pair.Key)
                       .Append('=')
                       .Append(pair.Value.ToString())
                       .Append('\0');
            }

            string data = builder.ToString();

            // Attempt up to five times to write to the cache
            for (int i = 0; i < 5; i += 1)
            {
                try
                {
                    // Just open the file from disk, the tenant identities are not secret and
                    // therefore safely left as unencrypted plain text.
                    using (var stream = File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    using (var deflate = new GZipStream(stream, CompressionMode.Compress))
                    using (var writer = new StreamWriter(deflate, encoding))
                    {
                        await writer.WriteAsync(data);
                    }
                }
                catch when (i < 5)
                {
                    // Sleep the thread, and wait before trying again using progressive back off
                    System.Threading.Thread.Sleep(i + 1 * 100);
                }
                catch (Exception e)
                {
                    exception = e;
                }
            }

            if (exception != null)
            {
                System.Diagnostics.Debug.WriteLine(exception);

                Git.Trace.WriteLine($"failed to serialize tenant cache: {exception.GetType().Name}.");
            }
        }
    }
}
