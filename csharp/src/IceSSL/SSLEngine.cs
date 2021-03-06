// **********************************************************************
//
// Copyright (c) 2003-2016 ZeroC, Inc. All rights reserved.
//
// This copy of Ice is licensed to you under the terms described in the
// ICE_LICENSE file included in this distribution.
//
// **********************************************************************

namespace IceSSL
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.IO;
    using System.Security;
    using System.Security.Authentication;
    using System.Security.Cryptography;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using System.Globalization;

    internal class SSLEngine
    {
        internal SSLEngine(IceInternal.ProtocolPluginFacade facade)
        {
            _communicator = facade.getCommunicator();
            _logger = _communicator.getLogger();
            _facade = facade;
            _securityTraceLevel = _communicator.getProperties().getPropertyAsIntWithDefault("IceSSL.Trace.Security", 0);
            _securityTraceCategory = "Security";
            _initialized = false;
            _trustManager = new TrustManager(_communicator);
            _tls12Support = false;
            try
            {
                Enum.Parse(typeof(System.Security.Authentication.SslProtocols), "Tls12");
                _tls12Support = true;
            }
            catch(Exception)
            {
            }
        }

        internal void initialize()
        {
            if(_initialized)
            {
                return;
            }

            const string prefix = "IceSSL.";
            Ice.Properties properties = communicator().getProperties();

            //
            // Check for a default directory. We look in this directory for
            // files mentioned in the configuration.
            //
            _defaultDir = properties.getProperty(prefix + "DefaultDir");

            string certStoreLocation = properties.getPropertyWithDefault(prefix + "CertStoreLocation", "CurrentUser");
            StoreLocation storeLocation;
            if(certStoreLocation == "CurrentUser")
            {
                storeLocation = StoreLocation.CurrentUser;
            }
            else if(certStoreLocation == "LocalMachine")
            {
                storeLocation = StoreLocation.LocalMachine;
            }
            else
            {
                _logger.warning("Invalid IceSSL.CertStoreLocation value `" + certStoreLocation +
                                "' adjusted to `CurrentUser'");
                storeLocation = StoreLocation.CurrentUser;
            }
            _useMachineContext = certStoreLocation == "LocalMachine";

            //
            // Protocols selects which protocols to enable, by default we only enable TLS1.0
            // TLS1.1 and TLS1.2 to avoid security issues with SSLv3
            //
            _protocols = parseProtocols(
                properties.getPropertyAsListWithDefault(prefix + "Protocols",
                                                        _tls12Support ? new string[]{"TLS1_0", "TLS1_1", "TLS1_2"} :
                                                                        new string[]{"TLS1_0", "TLS1_1"}));
            //
            // CheckCertName determines whether we compare the name in a peer's
            // certificate against its hostname.
            //
            _checkCertName = properties.getPropertyAsIntWithDefault(prefix + "CheckCertName", 0) > 0;

            //
            // VerifyDepthMax establishes the maximum length of a peer's certificate
            // chain, including the peer's certificate. A value of 0 means there is
            // no maximum.
            //
            _verifyDepthMax = properties.getPropertyAsIntWithDefault(prefix + "VerifyDepthMax", 3);

            //
            // CheckCRL determines whether the certificate revocation list is checked, and how strictly.
            //
            _checkCRL = properties.getPropertyAsIntWithDefault(prefix + "CheckCRL", 0);

            //
            // Check for a certificate verifier.
            //
            string certVerifierClass = properties.getProperty(prefix + "CertVerifier");
            if(certVerifierClass.Length > 0)
            {
                if(_verifier != null)
                {
                    Ice.PluginInitializationException e = new Ice.PluginInitializationException();
                    e.reason = "IceSSL: certificate verifier already installed";
                    throw e;
                }

                Type cls = _facade.findType(certVerifierClass);
                if(cls == null)
                {
                    Ice.PluginInitializationException e = new Ice.PluginInitializationException();
                    e.reason = "IceSSL: unable to load certificate verifier class " + certVerifierClass;
                    throw e;
                }

                try
                {
                    _verifier = (CertificateVerifier)IceInternal.AssemblyUtil.createInstance(cls);
                }
                catch(Exception ex)
                {
                    Ice.PluginInitializationException e = new Ice.PluginInitializationException(ex);
                    e.reason = "IceSSL: unable to instantiate certificate verifier class " + certVerifierClass;
                    throw e;
                }

                if(_verifier == null)
                {
                    Ice.PluginInitializationException e = new Ice.PluginInitializationException();
                    e.reason = "IceSSL: unable to instantiate certificate verifier class " + certVerifierClass;
                    throw e;
                }
            }

            //
            // Check for a password callback.
            //
            string passwordCallbackClass = properties.getProperty(prefix + "PasswordCallback");
            if(passwordCallbackClass.Length > 0)
            {
                if(_passwordCallback != null)
                {
                    Ice.PluginInitializationException e = new Ice.PluginInitializationException();
                    e.reason = "IceSSL: password callback already installed";
                    throw e;
                }

                Type cls = _facade.findType(passwordCallbackClass);
                if(cls == null)
                {
                    Ice.PluginInitializationException e = new Ice.PluginInitializationException();
                    e.reason = "IceSSL: unable to load password callback class " + passwordCallbackClass;
                    throw e;
                }

                try
                {
                    _passwordCallback = (PasswordCallback)IceInternal.AssemblyUtil.createInstance(cls);
                }
                catch(Exception ex)
                {
                    Ice.PluginInitializationException e = new Ice.PluginInitializationException(ex);
                    e.reason = "IceSSL: unable to load password callback class " + passwordCallbackClass;
                    throw e;
                }

                if(_passwordCallback == null)
                {
                    Ice.PluginInitializationException e = new Ice.PluginInitializationException();
                    e.reason = "IceSSL: unable to load password callback class " + passwordCallbackClass;
                    throw e;
                }
            }

            //
            // If the user hasn't supplied a certificate collection, we need to examine
            // the property settings.
            //
            if(_certs == null)
            {
                //
                // If IceSSL.CertFile is defined, load a certificate from a file and
                // add it to the collection.
                //
                // TODO: tracing?
                _certs = new X509Certificate2Collection();
                string certFile = properties.getProperty(prefix + "CertFile");
                string passwordStr = properties.getProperty(prefix + "Password");
                string findCert = properties.getProperty(prefix + "FindCert");
                const string findPrefix = prefix + "FindCert.";
                Dictionary<string, string> findCertProps = properties.getPropertiesForPrefix(findPrefix);

                if(certFile.Length > 0)
                {
                    if(!checkPath(ref certFile))
                    {
                        Ice.PluginInitializationException e = new Ice.PluginInitializationException();
                        e.reason = "IceSSL: certificate file not found: " + certFile;
                        throw e;
                    }

                    SecureString password = null;
                    if(passwordStr.Length > 0)
                    {
                        password = createSecureString(passwordStr);
                    }
                    else if(_passwordCallback != null)
                    {
                        password = _passwordCallback.getPassword(certFile);
                    }

                    try
                    {
                        X509Certificate2 cert;
                        X509KeyStorageFlags importFlags;
                        if(_useMachineContext)
                        {
                            importFlags = X509KeyStorageFlags.MachineKeySet;
                        }
                        else
                        {
                            importFlags = X509KeyStorageFlags.UserKeySet;
                        }

                        if(password != null)
                        {
                            cert = new X509Certificate2(certFile, password, importFlags);
                        }
                        else
                        {
                            cert = new X509Certificate2(certFile, "", importFlags);
                        }
                        _certs.Add(cert);
                    }
                    catch(CryptographicException ex)
                    {
                        Ice.PluginInitializationException e = new Ice.PluginInitializationException(ex);
                        e.reason = "IceSSL: error while attempting to load certificate from " + certFile;
                        throw e;
                    }
                }
                else if(findCert.Length > 0)
                {
                    string certStore = properties.getPropertyWithDefault("IceSSL.CertStore", "My");
                    _certs.AddRange(findCertificates("IceSSL.FindCert", storeLocation, certStore, findCert));
                    if(_certs.Count == 0)
                    {
                        throw new Ice.PluginInitializationException("IceSSL: no certificates found");
                    }
                }
                else if(findCertProps.Count > 0)
                {
                    //
                    // If IceSSL.FindCert.* properties are defined, add the selected certificates
                    // to the collection.
                    //
                    foreach(KeyValuePair<string, string> entry in findCertProps)
                    {
                        string name = entry.Key;
                        string val = entry.Value;
                        if(val.Length > 0)
                        {
                            string storeSpec = name.Substring(findPrefix.Length);
                            StoreLocation storeLoc = 0;
                            StoreName storeName = 0;
                            string sname = null;
                            parseStore(name, storeSpec, ref storeLoc, ref storeName, ref sname);
                            if(sname == null)
                            {
                                sname = storeName.ToString();
                            }
                            X509Certificate2Collection coll = findCertificates(name, storeLoc, sname, val);
                            _certs.AddRange(coll);
                        }
                    }
                    if(_certs.Count == 0)
                    {
                        Ice.PluginInitializationException e = new Ice.PluginInitializationException();
                        e.reason = "IceSSL: no certificates found";
                        throw e;
                    }
                }
            }

            if(_caCerts == null)
            {
                string certAuthFile = properties.getProperty(prefix + "CAs");
                if(certAuthFile.Length == 0)
                {
                    certAuthFile = properties.getProperty(prefix + "CertAuthFile");
                }
                if(certAuthFile.Length > 0 || properties.getPropertyAsInt(prefix + "UsePlatformCAs") <= 0)
                {
                    _caCerts = new X509Certificate2Collection();
                }
                if(certAuthFile.Length > 0)
                {
                    if(!checkPath(ref certAuthFile))
                    {
                        Ice.PluginInitializationException e = new Ice.PluginInitializationException();
                        e.reason = "IceSSL: CA certificate file not found: " + certAuthFile;
                        throw e;
                    }

                    try
                    {
                        using(System.IO.FileStream fs = System.IO.File.OpenRead(certAuthFile))
                        {
                            byte[] data = new byte[fs.Length];
                            fs.Read(data, 0, data.Length);

                            string strbuf = "";
                            try
                            {
                                strbuf = System.Text.Encoding.UTF8.GetString(data);
                            }
                            catch(Exception)
                            {
                                // Ignore
                            }

                            if(strbuf.Length == data.Length)
                            {
                                int size, startpos, endpos = 0;
                                bool first = true;
                                while(true)
                                {
                                    startpos = strbuf.IndexOf("-----BEGIN CERTIFICATE-----", endpos);
                                    if(startpos != -1)
                                    {
                                        endpos = strbuf.IndexOf("-----END CERTIFICATE-----", startpos);
                                        size = endpos - startpos + "-----END CERTIFICATE-----".Length;
                                    }
                                    else if(first)
                                    {
                                        startpos = 0;
                                        endpos = strbuf.Length;
                                        size = strbuf.Length;
                                    }
                                    else
                                    {
                                        break;
                                    }

                                    byte[] cert = new byte[size];
                                    System.Buffer.BlockCopy(data, startpos, cert, 0, size);
                                    _caCerts.Import(cert);
                                    first = false;
                                }
                            }
                            else
                            {
                                _caCerts.Import(data);
                            }
                        }
                    }
                    catch(Exception ex)
                    {
                        Ice.PluginInitializationException e = new Ice.PluginInitializationException(ex);
                        e.reason = "IceSSL: error while attempting to load CA certificate from " + certAuthFile;
                        throw e;
                    }
                }
            }
            _initialized = true;
        }

        internal bool useMachineContext()
        {
            return _useMachineContext;
        }

        internal X509Certificate2Collection caCerts()
        {
            return _caCerts;
        }

        internal void setCACertificates(X509Certificate2Collection caCerts)
        {
            if(_initialized)
            {
                Ice.PluginInitializationException e = new Ice.PluginInitializationException();
                e.reason = "IceSSL: plug-in is already initialized";
                throw e;
            }

            _caCerts = caCerts;
        }

        internal void setCertificates(X509Certificate2Collection certs)
        {
            if(_initialized)
            {
                Ice.PluginInitializationException e = new Ice.PluginInitializationException();
                e.reason = "IceSSL: plug-in is already initialized";
                throw e;
            }

            _certs = certs;
        }

        internal void setCertificateVerifier(CertificateVerifier verifier)
        {
            _verifier = verifier;
        }

        internal CertificateVerifier getCertificateVerifier()
        {
            return _verifier;
        }

        internal void setPasswordCallback(PasswordCallback callback)
        {
            _passwordCallback = callback;
        }

        internal PasswordCallback getPasswordCallback()
        {
            return _passwordCallback;
        }

        internal Ice.Communicator communicator()
        {
            return _facade.getCommunicator();
        }

        internal int securityTraceLevel()
        {
            return _securityTraceLevel;
        }

        internal string securityTraceCategory()
        {
            return _securityTraceCategory;
        }

        internal bool initialized()
        {
            return _initialized;
        }

        internal X509Certificate2Collection certs()
        {
            return _certs;
        }

        internal SslProtocols protocols()
        {
            return _protocols;
        }

        internal int checkCRL()
        {
            return _checkCRL;
        }

        internal void traceStream(System.Net.Security.SslStream stream, string connInfo)
        {
            System.Text.StringBuilder s = new System.Text.StringBuilder();
            s.Append("SSL connection summary");
            if(connInfo.Length > 0)
            {
                s.Append("\n");
                s.Append(connInfo);
            }
            s.Append("\nauthenticated = " + (stream.IsAuthenticated ? "yes" : "no"));
            s.Append("\nencrypted = " + (stream.IsEncrypted ? "yes" : "no"));
            s.Append("\nsigned = " + (stream.IsSigned ? "yes" : "no"));
            s.Append("\nmutually authenticated = " + (stream.IsMutuallyAuthenticated ? "yes" : "no"));
            s.Append("\nhash algorithm = " + stream.HashAlgorithm + "/" + stream.HashStrength);
            s.Append("\ncipher algorithm = " + stream.CipherAlgorithm + "/" + stream.CipherStrength);
            s.Append("\nkey exchange algorithm = " + stream.KeyExchangeAlgorithm + "/" + stream.KeyExchangeStrength);
            s.Append("\nprotocol = " + stream.SslProtocol);
            _logger.trace(_securityTraceCategory, s.ToString());
        }

        internal void verifyPeer(string address, NativeConnectionInfo info, string desc)
        {
            //
            // For an outgoing connection, we compare the proxy address (if any) against
            // fields in the server's certificate (if any).
            //
            if(info.nativeCerts != null && info.nativeCerts.Length > 0 && address.Length > 0)
            {
                //
                // Extract the IP addresses and the DNS names from the subject
                // alternative names.
                //
                List<string> dnsNames = null;
                List<string> ipAddresses = null;

                //
                // Search for "subject alternative name" extensions. The OID value
                // of interest is 2.5.29.17 and the encoded data has the following
                // ASN.1 syntax:
                //
                // GeneralNames ::= SEQUENCE SIZE (1..MAX) OF GeneralName
                //
                // GeneralName ::= CHOICE {
                //    otherName                       [0]     OtherName,
                //    rfc822Name                      [1]     IA5String,
                //    dNSName                         [2]     IA5String,
                //    x400Address                     [3]     ORAddress,
                //    directoryName                   [4]     Name,
                //    ediPartyName                    [5]     EDIPartyName,
                //    uniformResourceIdentifier       [6]     IA5String,
                //    iPAddress                       [7]     OCTET STRING,
                //    registeredID                    [8]     OBJECT IDENTIFIER
                // }
                //
                foreach(X509Extension ext in info.nativeCerts[0].Extensions)
                {
                    if(ext.Oid.Value.Equals("2.5.29.17") && ext.RawData.Length > 0)
                    {
                        byte[] data = ext.RawData;
                        if(data.Length < 2 || data[0] != 0x30) // ASN.1 sequence
                        {
                            continue;
                        }

                        int seqLen, pos;
                        if(!decodeASN1Length(data, 1, out seqLen, out pos))
                        {
                            continue;
                        }

                        while(pos < data.Length)
                        {
                            int tag = data[pos];

                            int len;
                            if(!decodeASN1Length(data, pos + 1, out len, out pos))
                            {
                                break;
                            }

                            if(tag == 0x82)
                            {
                                //
                                // Extract DNS name.
                                //
                                StringBuilder b = new StringBuilder();
                                for(int j = pos; j < pos + len; ++j)
                                {
                                    b.Append((char)data[j]);
                                }
                                if(dnsNames == null)
                                {
                                    dnsNames = new List<string>();
                                }
                                dnsNames.Add(b.ToString().ToUpperInvariant());
                            }
                            else if(tag == 0x87)
                            {
                                //
                                // Extract IP address.
                                //
                                char sep = len == 4 ? '.' : ':';
                                StringBuilder b = new StringBuilder();
                                for(int j = pos; j < pos + len; ++j)
                                {
                                    if(j > pos)
                                    {
                                        b.Append(sep);
                                    }
                                    b.Append(data[j].ToString(CultureInfo.InvariantCulture));
                                }
                                if(ipAddresses == null)
                                {
                                    ipAddresses = new List<string>();
                                }
                                ipAddresses.Add(b.ToString().ToUpperInvariant());
                            }

                            pos += len;
                        }
                    }
                }

                //
                // Compare the peer's address against the common name as well as
                // the dnsName and ipAddress values in the subject alternative name.
                //
                string dn = info.nativeCerts[0].Subject;
                string addrLower = address.ToUpperInvariant();
                bool certNameOK = false;
                {
                    string cn = "cn=" + addrLower;
                    int pos = dn.ToLower(CultureInfo.InvariantCulture).IndexOf(cn, StringComparison.Ordinal);
                    if(pos >= 0)
                    {
                        //
                        // Ensure we match the entire common name.
                        //
                        certNameOK = (pos + cn.Length == dn.Length) || (dn[pos + cn.Length] == ',');
                    }
                }

                //
                // Compare the peer's address against the dnsName and ipAddress
                // values in the subject alternative name.
                //
                if(!certNameOK && ipAddresses != null)
                {
                    certNameOK = ipAddresses.Contains(addrLower);
                }
                if(!certNameOK && dnsNames != null)
                {
                    certNameOK = dnsNames.Contains(addrLower);
                }

                //
                // Log a message if the name comparison fails. If CheckCertName is defined,
                // we also raise an exception to abort the connection. Don't log a message if
                // CheckCertName is not defined and a verifier is present.
                //
                if(!certNameOK && (_checkCertName || (_securityTraceLevel >= 1 && _verifier == null)))
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append("IceSSL: ");
                    if(!_checkCertName)
                    {
                        sb.Append("ignoring ");
                    }
                    sb.Append("certificate validation failure:\npeer certificate does not have `");
                    sb.Append(address);
                    sb.Append("' as its commonName or in its subjectAltName extension");
                    if(dn.Length > 0)
                    {
                        sb.Append("\nSubject DN: ");
                        sb.Append(dn);
                    }
                    if(dnsNames != null)
                    {
                        sb.Append("\nDNS names found in certificate: ");
                        for(int j = 0; j < dnsNames.Count; ++j)
                        {
                            if(j > 0)
                            {
                                sb.Append(", ");
                            }
                            sb.Append(dnsNames[j]);
                        }
                    }
                    if(ipAddresses != null)
                    {
                        sb.Append("\nIP addresses found in certificate: ");
                        for(int j = 0; j < ipAddresses.Count; ++j)
                        {
                            if(j > 0)
                            {
                                sb.Append(", ");
                            }
                            sb.Append(ipAddresses[j]);
                        }
                    }
                    string msg = sb.ToString();
                    if(_securityTraceLevel >= 1)
                    {
                        _logger.trace(_securityTraceCategory, msg);
                    }
                    if(_checkCertName)
                    {
                        Ice.SecurityException ex = new Ice.SecurityException();
                        ex.reason = msg;
                        throw ex;
                    }
                }
            }

            if(_verifyDepthMax > 0 && info.nativeCerts != null && info.nativeCerts.Length > _verifyDepthMax)
            {
                string msg = (info.incoming ? "incoming" : "outgoing") + " connection rejected:\n" +
                    "length of peer's certificate chain (" + info.nativeCerts.Length + ") exceeds maximum of " +
                    _verifyDepthMax + "\n" + desc;
                if(_securityTraceLevel >= 1)
                {
                    _logger.trace(_securityTraceCategory, msg);
                }
                Ice.SecurityException ex = new Ice.SecurityException();
                ex.reason = msg;
                throw ex;
            }

            if(!_trustManager.verify(info, desc))
            {
                string msg = (info.incoming ? "incoming" : "outgoing") + " connection rejected by trust manager\n" +
                    desc;
                if(_securityTraceLevel >= 1)
                {
                    _logger.trace(_securityTraceCategory, msg);
                }

                Ice.SecurityException ex = new Ice.SecurityException();
                ex.reason = "IceSSL: " + msg;
                throw ex;
            }

            if(_verifier != null && !_verifier.verify(info))
            {
                string msg = (info.incoming ? "incoming" : "outgoing") +
                    " connection rejected by certificate verifier\n" + desc;
                if(_securityTraceLevel >= 1)
                {
                    _logger.trace(_securityTraceCategory, msg);
                }

                Ice.SecurityException ex = new Ice.SecurityException();
                ex.reason = "IceSSL: " + msg;
                throw ex;
            }
        }

        //
        // Parse a string of the form "location.name" into two parts.
        //
        private static void parseStore(string prop, string store, ref StoreLocation loc, ref StoreName name,
                                       ref string sname)
        {
            int pos = store.IndexOf('.');
            if(pos == -1)
            {
                Ice.PluginInitializationException e = new Ice.PluginInitializationException();
                e.reason = "IceSSL: property `" + prop + "' has invalid format";
                throw e;
            }

            string sloc = store.Substring(0, pos).ToUpperInvariant();
            if(sloc.Equals("CURRENTUSER"))
            {
                loc = StoreLocation.CurrentUser;
            }
            else if(sloc.Equals("LOCALMACHINE"))
            {
                loc = StoreLocation.LocalMachine;
            }
            else
            {
                Ice.PluginInitializationException e = new Ice.PluginInitializationException();
                e.reason = "IceSSL: unknown store location `" + sloc + "' in " + prop;
                throw e;
            }

            sname = store.Substring(pos + 1);
            if(sname.Length == 0)
            {
                Ice.PluginInitializationException e = new Ice.PluginInitializationException();
                e.reason = "IceSSL: invalid store name in " + prop;
                throw e;
            }

            //
            // Try to convert the name into the StoreName enumeration.
            //
            try
            {
                name = (StoreName)Enum.Parse(typeof(StoreName), sname, true);
                sname = null;
            }
            catch(ArgumentException)
            {
                // Ignore - assume the user is selecting a non-standard store.
            }
        }

        private static bool isAbsolutePath(string path)
        {
            //
            // Skip whitespace
            //
            path = path.Trim();

            //
            // We need at least 3 non-whitespace characters to have an absolute path
            //
            if(path.Length < 3)
            {
                return false;
            }

            //
            // Check for X:\ path ('\' may have been converted to '/')
            //
            if((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z'))
            {
                return path[1] == ':' && (path[2] == '\\' || path[2] == '/');
            }

            //
            // Check for UNC path
            //
            return (path[0] == '\\' && path[1] == '\\') || path[0] == '/';
        }

        private bool checkPath(ref string path)
        {
            if(File.Exists(path))
            {
                return true;
            }

            if(_defaultDir.Length > 0 && !isAbsolutePath(path))
            {
                string s = _defaultDir + Path.DirectorySeparatorChar + path;
                if(File.Exists(s))
                {
                    path = s;
                    return true;
                }
            }

            return false;
        }

        private SslProtocols parseProtocols(string[] arr)
        {
            SslProtocols result = SslProtocols.Default;

            if(arr.Length > 0)
            {
                result = 0;
                for(int i = 0; i < arr.Length; ++i)
                {
                    string protocol = null;
                    string s = arr[i].ToUpperInvariant();
                    switch(s)
                    {
                        case "SSL3":
                        case "SSLV3":
                        {
                            protocol = "Ssl3";
                            break;
                        }
                        case "TLS":
                        case "TLS1":
                        case "TLS1_0":
                        case "TLSV1":
                        case "TLSV1_0":
                        {
                            protocol = "Tls";
                            break;
                        }
                        case "TLS1_1":
                        case "TLSV1_1":
                        {
                            protocol = "Tls11";
                            break;
                        }
                        case "TLS1_2":
                        case "TLSV1_2":
                        {
                            protocol = "Tls12";
                            break;
                        }
                        default:
                        {
                            break;
                        }
                    }

                    try
                    {
                        SslProtocols value = (SslProtocols)Enum.Parse(typeof(SslProtocols), protocol);
                        result |= value;
                    }
                    catch(Exception)
                    {
                        Ice.PluginInitializationException e = new Ice.PluginInitializationException();
                        e.reason = "IceSSL: unrecognized protocol `" + s + "'";
                        throw e;
                    }
                }
            }
            return result;
        }

        private static X509Certificate2Collection findCertificates(string prop, StoreLocation storeLocation,
                                                                   string name, string value)
        {
            //
            // Open the X509 certificate store.
            //
            X509Store store = null;
            try
            {
                try
                {
                    store = new X509Store((StoreName)Enum.Parse(typeof(StoreName), name, true), storeLocation);
                }
                catch(ArgumentException)
                {
                    store = new X509Store(name, storeLocation);
                }
                store.Open(OpenFlags.ReadOnly);
            }
            catch(Exception ex)
            {
                Ice.PluginInitializationException e = new Ice.PluginInitializationException(ex);
                e.reason = "IceSSL: failure while opening store specified by " + prop;
                throw e;
            }

            //
            // Start with all of the certificates in the collection and filter as necessary.
            //
            // - If the value is "*", return all certificates.
            // - Otherwise, search using key:value pairs. The following keys are supported:
            //
            //   Issuer
            //   IssuerDN
            //   Serial
            //   Subject
            //   SubjectDN
            //   SubjectKeyId
            //   Thumbprint
            //
            //   A value must be enclosed in single or double quotes if it contains whitespace.
            //
            X509Certificate2Collection result = new X509Certificate2Collection();
            result.AddRange(store.Certificates);
            try
            {
                if(value != "*")
                {
                    if(value.IndexOf(':') == -1)
                    {
                        Ice.PluginInitializationException e = new Ice.PluginInitializationException();
                        e.reason = "IceSSL: no key in `" + value + "'";
                        throw e;
                    }
                    int start = 0;
                    int pos;
                    while((pos = value.IndexOf(':', start)) != -1)
                    {
                        //
                        // Parse the X509FindType.
                        //
                        string field = value.Substring(start, pos - start).Trim().ToUpperInvariant();
                        X509FindType findType;
                        if(field.Equals("SUBJECT"))
                        {
                            findType = X509FindType.FindBySubjectName;
                        }
                        else if(field.Equals("SUBJECTDN"))
                        {
                            findType = X509FindType.FindBySubjectDistinguishedName;
                        }
                        else if(field.Equals("ISSUER"))
                        {
                            findType = X509FindType.FindByIssuerName;
                        }
                        else if(field.Equals("ISSUERDN"))
                        {
                            findType = X509FindType.FindByIssuerDistinguishedName;
                        }
                        else if(field.Equals("THUMBPRINT"))
                        {
                            findType = X509FindType.FindByThumbprint;
                        }
                        else if(field.Equals("SUBJECTKEYID"))
                        {
                            findType = X509FindType.FindBySubjectKeyIdentifier;
                        }
                        else if(field.Equals("SERIAL"))
                        {
                            findType = X509FindType.FindBySerialNumber;
                        }
                        else
                        {
                            Ice.PluginInitializationException e = new Ice.PluginInitializationException();
                            e.reason = "IceSSL: unknown key in `" + value + "'";
                            throw e;
                        }

                        //
                        // Parse the argument.
                        //
                        start = pos + 1;
                        while(start < value.Length && (value[start] == ' ' || value[start] == '\t'))
                        {
                            ++start;
                        }
                        if(start == value.Length)
                        {
                            Ice.PluginInitializationException e = new Ice.PluginInitializationException();
                            e.reason = "IceSSL: missing argument in `" + value + "'";
                            throw e;
                        }

                        string arg;
                        if(value[start] == '"' || value[start] == '\'')
                        {
                            int end = start;
                            ++end;
                            while(end < value.Length)
                            {
                                if(value[end] == value[start] && value[end - 1] != '\\')
                                {
                                    break;
                                }
                                ++end;
                            }
                            if(end == value.Length || value[end] != value[start])
                            {
                                Ice.PluginInitializationException e = new Ice.PluginInitializationException();
                                e.reason = "IceSSL: unmatched quote in `" + value + "'";
                                throw e;
                            }
                            ++start;
                            arg = value.Substring(start, end - start);
                            start = end + 1;
                        }
                        else
                        {
                            char[] ws = new char[] { ' ', '\t' };
                            int end = value.IndexOfAny(ws, start);
                            if(end == -1)
                            {
                                arg = value.Substring(start);
                                start = value.Length;
                            }
                            else
                            {
                                arg = value.Substring(start, end - start);
                                start = end + 1;
                            }
                        }

                        //
                        // Execute the query.
                        //
                        // TODO: allow user to specify a value for validOnly?
                        //
                        bool validOnly = false;
                        if(findType == X509FindType.FindBySubjectDistinguishedName ||
                           findType == X509FindType.FindByIssuerDistinguishedName)
                        {
                            X500DistinguishedNameFlags[] flags = {
                                X500DistinguishedNameFlags.None,
                                X500DistinguishedNameFlags.Reversed,
                            };
                            X500DistinguishedName dn = new X500DistinguishedName(arg);
                            X509Certificate2Collection r = result;
                            for(int i = 0; i < flags.Length; ++i)
                            {
                                r = result.Find(findType, dn.Decode(flags[i]), validOnly);
                                if(r.Count > 0)
                                {
                                    break;
                                }
                            }
                            result = r;
                        }
                        else
                        {
                            result = result.Find(findType, arg, validOnly);
                        }
                    }
                }
            }
            finally
            {
                store.Close();
            }

            return result;
        }

        private static SecureString createSecureString(string s)
        {
            SecureString result = new SecureString();
            foreach(char ch in s)
            {
                result.AppendChar(ch);
            }
            return result;
        }

        private static bool decodeASN1Length(byte[] data, int start, out int len, out int next)
        {
            len = 0;
            next = 0;

            if(start + 1 > data.Length)
            {
                return false;
            }

            len = data[start];
            int len2 = 0;
            if(len > 0x80) // Composed length
            {
                len2 = len - 0x80;
                if(start + len2 + 1 > data.Length)
                {
                    return false;
                }
                len = 0;
                for(int i = 0; i < len2; i++)
                {
                    len *= 256;
                    len += data[start + i + 1];
                }
            }
            else if(len == 0x80) // Undefined length encoding
            {
                return false;
            }

            next = start + len2 + 1;
            return (next + len <= data.Length);
        }

        private Ice.Communicator _communicator;
        private Ice.Logger _logger;
        private IceInternal.ProtocolPluginFacade _facade;
        private int _securityTraceLevel;
        private string _securityTraceCategory;
        private bool _initialized;
        private string _defaultDir;
        private SslProtocols _protocols;
        private bool _checkCertName;
        private int _verifyDepthMax;
        private int _checkCRL;
        private X509Certificate2Collection _certs;
        private bool _useMachineContext;
        private X509Certificate2Collection _caCerts;
        private CertificateVerifier _verifier;
        private PasswordCallback _passwordCallback;
        private TrustManager _trustManager;
        private bool _tls12Support;
    }
}
