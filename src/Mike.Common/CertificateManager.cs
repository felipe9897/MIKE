using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Mike.Common
{
    public class CertificateManager
    {
        private readonly string _certDir;
        private const string CaCertFile = "ca.crt";
        private const string CaKeyFile = "ca.key";
        private const string ClientCertFile = "client.crt";
        private const string ClientKeyFile = "client.key";

        public CertificateManager()
        {
            _certDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MikeLocal", "certs");
            Directory.CreateDirectory(_certDir);
        }

        public X509Certificate2 GetClientCertificate()
        {
            string certPath = Path.Combine(_certDir, ClientCertFile);
            string keyPath = Path.Combine(_certDir, ClientKeyFile);

            if (!File.Exists(certPath) || !File.Exists(keyPath))
            {
                ProvisionCertificates();
            }

            // Note: In a real .NET implementation, we'd use X509Certificate2.CreateFromPem
            // or load from a PFX. For simplicity in this environment, we'll assume
            // we can load them.
            return X509Certificate2.CreateFromPemFile(certPath, keyPath);
        }

        public X509Certificate2 GetCaCertificate()
        {
            string caPath = Path.Combine(_certDir, CaCertFile);
            if (!File.Exists(caPath))
            {
                ProvisionCertificates();
            }
            return X509CertificateLoader.LoadCertificateFromFile(caPath);
        }

        private void ProvisionCertificates()
        {
            // In a real-world scenario, the CA would be hosted centrally.
            // For the Local Mesh, we implement a "First Node is CA" or "Pre-shared CA" logic.
            // Here, we simulate the provision using openssl commands via process start
            // to ensure real certificates are created.

            RunCommand($"openssl genrsa -out {Path.Combine(_certDir, CaKeyFile)} 2048");
            RunCommand($"openssl req -x509 -new -nodes -key {Path.Combine(_certDir, CaKeyFile)} -sha256 -days 3650 -out {Path.Combine(_certDir, CaCertFile)} -subj \"/CN=MikeLocalCA\"");
            RunCommand($"openssl genrsa -out {Path.Combine(_certDir, ClientKeyFile)} 2048");
            RunCommand($"openssl req -new -key {Path.Combine(_certDir, ClientKeyFile)} -out {Path.Combine(_certDir, "client.csr")} -subj \"/CN=MikeLocalNode\"");
            RunCommand($"openssl x509 -req -in {Path.Combine(_certDir, "client.csr")} -CA {Path.Combine(_certDir, CaCertFile)} -CAkey {Path.Combine(_certDir, CaKeyFile)} -CAcreateserial -out {Path.Combine(_certDir, ClientCertFile)} -days 365 -sha256");
        }

        private void RunCommand(string command)
        {
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit();
        }

        public void RotateClientCertificate()
        {
            File.Delete(Path.Combine(_certDir, ClientCertFile));
            File.Delete(Path.Combine(_certDir, ClientKeyFile));
            ProvisionCertificates();
        }
    }
}
