using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SnapEye.Services
{
    /// <summary>
    /// Manages user session and token storage with encryption
    /// Stores tokens securely for 90 days
    /// </summary>
    public class SessionManager
    {
        private static readonly string AppDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SnapEye"
        );
        private static readonly string SessionFilePath = Path.Combine(AppDataPath, "session.dat");
        // AES-256 requires exactly 32 bytes for key and 16 bytes for IV
        private static readonly byte[] EncryptionKey = Encoding.UTF8.GetBytes("SnapEye2024SecureKey32BytesLong!"); // Exactly 32 bytes (31 + !)
        private static readonly byte[] EncryptionIV = Encoding.UTF8.GetBytes("SnapEyeInit16Byt"); // Exactly 16 bytes

        /// <summary>
        /// User session data
        /// </summary>
        public class SessionData
        {
            public string Username { get; set; } = string.Empty;
            public string AccessToken { get; set; } = string.Empty;
            public string RefreshToken { get; set; } = string.Empty;
            public string TokenType { get; set; } = "bearer";
            public int ExpiresIn { get; set; }
            public DateTime LoginTime { get; set; }
            public DateTime ExpiryTime { get; set; }
            public DateTime StorageExpiry { get; set; } // 90 days from login
        }

        /// <summary>
        /// Save session data securely (encrypted)
        /// </summary>
        public static void SaveSession(SessionData session)
        {
            try
            {
                // Ensure directory exists
                if (!Directory.Exists(AppDataPath))
                {
                    Directory.CreateDirectory(AppDataPath);
                }

                // Set storage expiry to 90 days from now
                session.StorageExpiry = DateTime.Now.AddDays(90);

                // Serialize to JSON
                string json = JsonSerializer.Serialize(session, new JsonSerializerOptions 
                { 
                    WriteIndented = true 
                });

                // Encrypt data
                byte[] encryptedData = EncryptString(json);

                // Save to file
                File.WriteAllBytes(SessionFilePath, encryptedData);

                Console.WriteLine($"SnapEye: Session saved for {session.Username}, valid until {session.StorageExpiry:yyyy-MM-dd HH:mm:ss}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SnapEye: Failed to save session - {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Load session data from storage
        /// Returns null if no valid session exists
        /// </summary>
        public static SessionData? LoadSession()
        {
            try
            {
                // Check if session file exists
                if (!File.Exists(SessionFilePath))
                {
                    Console.WriteLine("SnapEye: No saved session found");
                    return null;
                }

                // Read encrypted data
                byte[] encryptedData = File.ReadAllBytes(SessionFilePath);

                // Decrypt data
                string json = DecryptString(encryptedData);

                // Deserialize
                SessionData? session = JsonSerializer.Deserialize<SessionData>(json);

                if (session == null)
                {
                    Console.WriteLine("SnapEye: Session data is corrupted");
                    return null;
                }

                // Check if storage has expired (90 days)
                if (DateTime.Now > session.StorageExpiry)
                {
                    Console.WriteLine($"SnapEye: Session storage expired on {session.StorageExpiry:yyyy-MM-dd HH:mm:ss}");
                    DeleteSession();
                    return null;
                }

                // Check if token has expired
                if (DateTime.Now > session.ExpiryTime)
                {
                    Console.WriteLine($"SnapEye: Access token expired on {session.ExpiryTime:yyyy-MM-dd HH:mm:ss}");
                    // Don't delete session - might be able to refresh
                    return session;
                }

                Console.WriteLine($"SnapEye: Loaded session for {session.Username}");
                return session;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SnapEye: Failed to load session - {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Delete session data
        /// </summary>
        public static void DeleteSession()
        {
            try
            {
                if (File.Exists(SessionFilePath))
                {
                    File.Delete(SessionFilePath);
                    Console.WriteLine("SnapEye: Session deleted");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SnapEye: Failed to delete session - {ex.Message}");
            }
        }

        /// <summary>
        /// Check if a valid session exists
        /// </summary>
        public static bool HasValidSession()
        {
            SessionData? session = LoadSession();
            if (session == null) return false;

            // Valid if not expired
            return DateTime.Now <= session.ExpiryTime;
        }

        /// <summary>
        /// Check if session exists but token is expired (can be refreshed)
        /// </summary>
        public static bool HasExpiredSession()
        {
            SessionData? session = LoadSession();
            if (session == null) return false;

            // Expired if past token expiry but within storage window
            return DateTime.Now > session.ExpiryTime && DateTime.Now <= session.StorageExpiry;
        }

        /// <summary>
        /// Update session with refreshed token
        /// </summary>
        public static void UpdateSession(string newAccessToken, string newRefreshToken, int expiresIn)
        {
            SessionData? session = LoadSession();
            if (session == null)
            {
                throw new InvalidOperationException("No session to update");
            }

            session.AccessToken = newAccessToken;
            session.RefreshToken = newRefreshToken;
            session.ExpiresIn = expiresIn;
            session.ExpiryTime = DateTime.Now.AddSeconds(expiresIn - 60); // 60 second buffer

            SaveSession(session);
        }

        /// <summary>
        /// Encrypt string using AES
        /// </summary>
        private static byte[] EncryptString(string plainText)
        {
            using (Aes aes = Aes.Create())
            {
                aes.Key = EncryptionKey;
                aes.IV = EncryptionIV;

                ICryptoTransform encryptor = aes.CreateEncryptor(aes.Key, aes.IV);

                using (MemoryStream msEncrypt = new MemoryStream())
                {
                    using (CryptoStream csEncrypt = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write))
                    {
                        using (StreamWriter swEncrypt = new StreamWriter(csEncrypt))
                        {
                            swEncrypt.Write(plainText);
                        }
                        return msEncrypt.ToArray();
                    }
                }
            }
        }

        /// <summary>
        /// Decrypt string using AES
        /// </summary>
        private static string DecryptString(byte[] cipherText)
        {
            using (Aes aes = Aes.Create())
            {
                aes.Key = EncryptionKey;
                aes.IV = EncryptionIV;

                ICryptoTransform decryptor = aes.CreateDecryptor(aes.Key, aes.IV);

                using (MemoryStream msDecrypt = new MemoryStream(cipherText))
                {
                    using (CryptoStream csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read))
                    {
                        using (StreamReader srDecrypt = new StreamReader(csDecrypt))
                        {
                            return srDecrypt.ReadToEnd();
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Get time remaining until session expires
        /// </summary>
        public static TimeSpan? GetTimeUntilExpiry()
        {
            SessionData? session = LoadSession();
            if (session == null) return null;

            TimeSpan remaining = session.ExpiryTime - DateTime.Now;
            return remaining.TotalSeconds > 0 ? remaining : TimeSpan.Zero;
        }

        /// <summary>
        /// Get formatted expiry time string
        /// </summary>
        public static string GetExpiryString()
        {
            SessionData? session = LoadSession();
            if (session == null) return "No session";

            if (DateTime.Now > session.ExpiryTime)
            {
                return "Expired";
            }

            TimeSpan remaining = session.ExpiryTime - DateTime.Now;
            
            if (remaining.TotalDays >= 1)
            {
                return $"{(int)remaining.TotalDays} days";
            }
            else if (remaining.TotalHours >= 1)
            {
                return $"{(int)remaining.TotalHours} hours";
            }
            else
            {
                return $"{(int)remaining.TotalMinutes} minutes";
            }
        }
    }
}

