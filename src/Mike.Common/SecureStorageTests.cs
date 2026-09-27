using System;
using System.IO;

namespace Mike.Common
{
    public static class SecureStorageTests
    {
        public static bool RunTests()
        {
            try
            {
                string key = "test_key";
                string fixtureValue = "test-value-not-a-credential";
                
                SecureStorage.SaveSecret(key, fixtureValue);
                string? retrieved = SecureStorage.GetSecret(key);
                
                if (retrieved != fixtureValue) return false;
                
                // Test that it's actually encrypted on disk
                byte[] diskContent = File.ReadAllBytes(GetPath(key));
                string diskString = System.Text.Encoding.UTF8.GetString(diskContent);
                if (diskString.Contains(fixtureValue)) return false;

                return true;
            }
            catch { return false; }
        }

        private static string GetPath(string key)
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "MikeLocal", "secrets", $"{key}.bin");
        }
    }
}
